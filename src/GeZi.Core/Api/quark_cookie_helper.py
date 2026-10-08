# -*- coding: utf-8 -*-
"""
quark_cookie_helper.py —— 用 service_ticket 换取夸克登录 Cookie。

【为什么需要这个脚本】
夸克 `pan.quark.cn` 的前置 ALB/WAF 会按 TLS ClientHello 的指纹做过滤：
只有当 ClientHello 声明了足够多/足够新的椭圆曲线组（extension 足够大）时才放行，
否则直接 TCP RST。

- .NET Framework 的 SChannel：ClientHello 天然偏小 → **必然被 RST**
- 较老的 OpenSSL（如 3.0.x，随 Python 3.14 分发）：ClientHello 也偏小 → 被 RST
- 较新的 OpenSSL（3.2+，如 3.5.x）：ClientHello 足够大 → 放行

而登录态 Cookie（`__pus` / `__puus`）**只能**由 `pan.quark.cn/account/info?st=` 下发，
`uop.quark.cn` 只会下发无用的 `_UP_*` 系列。所以纯 .NET 无法完成扫码登录，
必须借一个"能被放行"的 TLS 栈来跑这一步 —— 本脚本就是干这个的。

【用法】
    python quark_cookie_helper.py <service_ticket>
    python quark_cookie_helper.py --selftest        # 自检：能否连上 pan.quark.cn

【输出】
    成功： stdout 打印一行 JSON  {"ok": true, "cookie": "...", "nickname": "...", "names": [...]}
    失败： stdout 打印一行 JSON  {"ok": false, "error": "...", "detail": "..."}
    所有诊断信息走 stderr，不污染 stdout 的 JSON。

【依赖】
    仅 Python 标准库，零第三方依赖。适用于 Python 3.6+。
"""

import sys
import json


def _emit(obj):
    """把结果以单行 JSON 写到 stdout。"""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False))
    sys.stdout.flush()


def _log(msg):
    """诊断信息走 stderr，绝不污染 stdout。"""
    try:
        sys.stderr.write(str(msg) + "\n")
        sys.stderr.flush()
    except Exception:
        pass


ACCOUNT_URL = "https://pan.quark.cn/account/info"
REFERER = "https://pan.quark.cn/"
API_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
          "(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 "
          "Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch")
DRIVE_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "Chrome/124.0 Safari/537.36")

# 关键登录 Cookie（缺它就没登录上）
LOGIN_COOKIE_KEY = "__pus"

ENRICH_URLS = (
    "https://pan.quark.cn/list",
    "https://drive-pc.quark.cn/1/clouddrive/file/sort"
    "?pr=ucpro&fr=pc&uc_param_str=&pdir_fid=0&_page=1&_size=50"
    "&_fetch_total=1&_sort=file_type:asc,updated_at:desc",
    "https://drive.quark.cn/1/clouddrive/member"
    "?pr=ucpro&fr=pc&uc_param_str=&fetch_subscribe=true",
)


def _base_headers():
    return {
        "User-Agent": API_UA,
        "Referer": REFERER,
        "Accept": "application/json, text/plain, */*",
        "Accept-Language": "zh-CN,zh;q=0.9",
    }


def _parse_cookie_dict(s):
    d = {}
    for part in (s or "").split(";"):
        part = part.strip()
        if not part or "=" not in part:
            continue
        k, v = part.split("=", 1)
        d[k.strip()] = v.strip()
    return d


def selftest():
    """自检：本解释器的 TLS 栈能否连上 pan.quark.cn。"""
    import ssl
    result = {"ok": True, "openssl": ssl.OPENSSL_VERSION, "reachable": {}}
    for host in ("pan.quark.cn", "uop.quark.cn", "drive-pc.quark.cn"):
        try:
            import socket
            ctx = ssl.create_default_context()
            raw = socket.create_connection((host, 443), timeout=8)
            s = ctx.wrap_socket(raw, server_hostname=host)
            result["reachable"][host] = s.version()
            s.close()
        except Exception as ex:
            result["reachable"][host] = "FAIL: %s: %s" % (type(ex).__name__, ex)
    result["can_exchange"] = not str(result["reachable"].get("pan.quark.cn", "")).startswith("FAIL")
    _emit(result)
    return 0 if result["can_exchange"] else 1


def exchange(ticket):
    import urllib.parse
    import urllib.request
    import urllib.error
    import http.cookiejar

    detail = []

    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPRedirectHandler(),
        urllib.request.HTTPCookieProcessor(jar),
    )

    # ---- 步骤1：用 service_ticket 在 pan.quark.cn 激活登录态 ----
    # 这一步会下发 __pus / __kp / __kps / __ktd / __uid / ctoken。
    hdrs = _base_headers()
    hdrs.update({
        "Origin": REFERER.rstrip("/"),
        "Accept": "text/html,application/xhtml+xml,*/*;q=0.8",
    })
    acct = "%s?st=%s&lw=scan" % (ACCOUNT_URL, urllib.parse.quote(ticket))
    nickname = ""
    try:
        with opener.open(urllib.request.Request(acct, headers=hdrs), timeout=20) as r:
            body = r.read().decode("utf-8", "replace")
        try:
            obj = json.loads(body)
            data = obj.get("data") or {}
            nickname = data.get("nickname") or ""
            if obj.get("success") is False:
                detail.append("account/info 返回失败: %s" % (obj.get("msg") or obj.get("code")))
        except Exception:
            pass
    except urllib.error.HTTPError as e:
        detail.append("account/info HTTP %s" % e.code)
    except Exception as e:
        detail.append("account/info 连接失败: %s: %s" % (type(e).__name__, e))

    # ---- 步骤2：请求网盘/下载接口，补全 __puus 等 ----
    for u in ENRICH_URLS:
        h = _base_headers()
        h["User-Agent"] = DRIVE_UA
        h["Origin"] = REFERER.rstrip("/")
        try:
            with opener.open(urllib.request.Request(u, headers=h), timeout=20) as r:
                r.read()
        except Exception as e:
            detail.append("%s 失败: %s" % (u.split("?")[0], type(e).__name__))

    # ---- 步骤3：汇总所有 quark.cn 域的 Cookie ----
    pairs = []
    for c in jar:
        if "quark.cn" in (c.domain or ""):
            pairs.append("%s=%s" % (c.name, c.value))
    cookie = "; ".join(pairs)
    names = [p.split("=", 1)[0] for p in pairs]

    if LOGIN_COOKIE_KEY in _parse_cookie_dict(cookie):
        _emit({"ok": True, "cookie": cookie, "nickname": nickname, "names": names})
        return 0

    _emit({
        "ok": False,
        "error": "未取回关键登录态(%s)" % LOGIN_COOKIE_KEY,
        "detail": " | ".join(detail) if detail else "无附加信息",
        "names": names,
    })
    return 2


def account_info(cookie):
    """只取账号信息（昵称 + 头像），不换 Cookie。

    【为什么要这个模式】`pan.quark.cn` 从 .NET（SChannel）根本连不上（WAF 按 ClientHello
    指纹过滤，见文件头说明），所以登录后想拿**昵称/头像**只能借这个脚本跑一次。
    实测返回：`data.nickname`（昵称）、`data.avatarUri`（头像地址，http://image.quark.cn/…）。

    用法:  python quark_cookie_helper.py --accountinfo "<cookie>"
    输出:  {"ok": true, "nickname": "...", "avatar": "...", "keys": [...]}
    """
    import urllib.request
    import urllib.error

    if not cookie or not cookie.strip():
        _emit({"ok": False, "error": "缺少 cookie 参数"})
        return 3

    hdrs = _base_headers()
    hdrs["Cookie"] = cookie.strip()
    url = ACCOUNT_URL + "?fr=pc&platform=pc"
    try:
        with urllib.request.urlopen(
                urllib.request.Request(url, headers=hdrs), timeout=20) as r:
            body = r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        _emit({"ok": False, "error": "HTTP %s" % e.code})
        return 2
    except Exception as e:
        _emit({"ok": False, "error": "%s: %s" % (type(e).__name__, e)})
        return 2

    try:
        obj = json.loads(body)
    except Exception:
        _emit({"ok": False, "error": "返回不是 JSON"})
        return 2

    if obj.get("success") is False:
        _emit({"ok": False,
               "error": "account/info 返回失败: %s" % (obj.get("msg") or obj.get("code"))})
        return 2

    data = obj.get("data") or {}
    # 头像字段实测叫 avatarUri；顺带兼容几个常见别名，取到第一个非空即可
    avatar = ""
    for k in ("avatarUri", "avatar", "avatarUrl", "avatar_url"):
        v = data.get(k)
        if isinstance(v, str) and v.strip():
            avatar = v.strip()
            break

    _emit({
        "ok": True,
        "nickname": (data.get("nickname") or "").strip(),
        "avatar": avatar,
        "keys": sorted(data.keys()),
    })
    return 0


def main():
    args = sys.argv[1:]
    if not args:
        _emit({"ok": False, "error": "缺少 service_ticket 参数"})
        return 3
    if args[0] == "--selftest":
        return selftest()
    if args[0] == "--accountinfo":
        if len(args) < 2:
            _emit({"ok": False, "error": "--accountinfo 需要 cookie 参数"})
            return 3
        return account_info(args[1])
    return exchange(args[0].strip())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as ex:
        _emit({"ok": False, "error": "%s: %s" % (type(ex).__name__, ex)})
        sys.exit(9)
