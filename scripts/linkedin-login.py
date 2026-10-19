#!/usr/bin/env python3
"""LinkedIn 一键登录 — 跑一次，永久有效。
   python3 scripts/linkedin-login.py
"""
import sys
from pathlib import Path

STATE = Path.home() / ".your-interview" / "linkedin-profile" / "state.json"

def main():
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        print("先装: pip install playwright && playwright install chromium")
        sys.exit(1)
    STATE.parent.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        b = p.chromium.launch(headless=False)
        ctx = b.new_context()
        pg = ctx.new_page()
        pg.goto("https://www.linkedin.com/login", timeout=30000)
        print("在弹出的窗口登录 LinkedIn，完成后回车...")
        input()
        pg.goto("https://www.linkedin.com/feed/", timeout=20000)
        if "login" in pg.url:
            print("还没登录成功，重跑一次。")
            b.close(); sys.exit(1)
        ctx.storage_state(path=str(STATE))
        print(f"OK，已保存，下次不用再登。")
        b.close()

if __name__ == "__main__":
    main()
