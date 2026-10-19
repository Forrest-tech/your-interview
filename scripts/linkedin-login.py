#!/usr/bin/env python3
"""
2026-10-19:LinkedIn 一键登录脚本 —— 为后端 Playwright 建立登录态。

用法:
    python3 scripts/linkedin-login.py

流程:
1. 启动有头 Chromium，打开 LinkedIn 登录页
2. 你在弹出的浏览器窗口里手动登录（支持 2FA/验证码）
3. 登录成功后按回车，会话保存到 ~/.your-interview/linkedin-profile/state.json
4. 后端 Docker 容器挂载该目录，之后 Auto-fetch 直接用登录态抓取

依赖: pip install playwright && playwright install chromium
"""
import os
import sys
from pathlib import Path

PROFILE_DIR = Path.home() / ".your-interview" / "linkedin-profile"
STATE_PATH = PROFILE_DIR / "state.json"


def main():
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        print("需要先安装: pip install playwright && playwright install chromium")
        sys.exit(1)

    PROFILE_DIR.mkdir(parents=True, exist_ok=True)
    print("正在启动浏览器...")
    print("请在弹出的 Chromium 窗口中登录 LinkedIn（支持扫码/2FA）。")
    print("登录成功进入首页后，回到这里按回车。\n")

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=False, args=["--no-sandbox"])
        ctx = browser.new_context(
            viewport={"width": 1280, "height": 900},
            user_agent="Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
                       "AppleWebKit/537.36 (KHTML, like Gecko) "
                       "Chrome/120.0.0.0 Safari/537.36",
        )
        page = ctx.new_page()
        # 先加载已有会话（二次运行可跳过登录）
        if STATE_PATH.exists():
            print(f"发现已有会话 {STATE_PATH}，尝试复用...")
            ctx2 = browser.new_context(storage_state=str(STATE_PATH))
            p2 = ctx2.new_page()
            p2.goto("https://www.linkedin.com/feed/", wait_until="domcontentloaded", timeout=20000)
            if "/login" not in p2.url and "/checkpoint" not in p2.url:
                print("已有会话有效，无需重新登录。")
                browser.close()
                return
            print("会话已过期，需要重新登录。")
            ctx2.close()

        page.goto("https://www.linkedin.com/login", wait_until="domcontentloaded", timeout=30000)
        input("登录完成后按回车保存会话... ")

        # 简单校验：进 feed 不跳登录页即算成功
        page.goto("https://www.linkedin.com/feed/", wait_until="domcontentloaded", timeout=20000)
        if "/login" in page.url or "/checkpoint" in page.url:
            print("看起来还没登录成功，会话未保存。请重跑脚本。")
            browser.close()
            sys.exit(1)

        ctx.storage_state(path=str(STATE_PATH))
        print(f"\n登录态已保存: {STATE_PATH}")
        print("之后 Smart paste 里贴 LinkedIn 链接点 Auto-fetch 即可。")
        browser.close()


if __name__ == "__main__":
    main()
