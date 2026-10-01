#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Claude Code 桌面版中文助手 (CCZhAssistant) - Linux / Ubuntu 通用版
面向 Linux (Ubuntu / Debian / Arch / Fedora 等) 的 Claude Desktop 极简外挂式界面中文汉化伴侣
"""

import os
import sys
import json
import glob
import shutil
import urllib.request
import subprocess
import platform
import argparse
import time
from pathlib import Path

APP_NAME = "Claude Code 桌面版中文助手"
APP_VERSION = "1.4.0"
MANIFEST_URL = "https://raw.githubusercontent.com/CX-ArtLab/Claude-Desktop-zh-assistant/main/translation/manifest.json"
DATA_DIR = Path.home() / ".local" / "share" / "Claude Code 中文助手"
BACKUP_DIR = DATA_DIR / "backups"
CONFIG_DIR = Path.home() / ".config" / "claude-desktop-zh-assistant"
AUTOSTART_FILE = Path.home() / ".config" / "autostart" / "claude-desktop-zh-assistant.desktop"

def get_resource_path(filename: str) -> Path:
    """获取资源文件路径（支持 PyInstaller 单文件打包与源码运行）"""
    candidates = []
    # PyInstaller 运行时解压目录
    if hasattr(sys, "_MEIPASS"):
        candidates.append(Path(sys._MEIPASS) / "Resources" / filename)
        candidates.append(Path(sys._MEIPASS) / filename)
    
    script_dir = Path(__file__).resolve().parent
    candidates.extend([
        script_dir / "Resources" / filename,
        script_dir / filename,
        script_dir.parent / "translation" / filename,
        script_dir.parent / "src" / "Assets" / filename,
    ])
    
    for c in candidates:
        if c.is_file():
            return c
    return candidates[0]

class ClaudeEnvironment:
    def __init__(self):
        self.is_installed = False
        self.is_running = False
        self.resources_path = None
        self.app_dir = None
        self.executable_path = None
        self.version = "未安装"

def detect_claude_environment() -> ClaudeEnvironment:
    """自动检测 Linux 下的 Claude Desktop 安装路径与运行状态"""
    env = ClaudeEnvironment()
    
    # 1. 检索正在运行的进程
    try:
        ps_output = subprocess.check_output(["ps", "-eo", "pid,args"], text=True, stderr=subprocess.DEVNULL)
        for line in ps_output.splitlines():
            line_lower = line.lower()
            if "claude-desktop" in line_lower or ("claude" in line_lower and "electron" in line_lower):
                env.is_running = True
                parts = line.strip().split()
                if len(parts) >= 2:
                    pid = parts[0]
                    try:
                        exe_link = os.readlink(f"/proc/{pid}/exe")
                        if os.path.isfile(exe_link):
                            env.executable_path = Path(exe_link)
                            parent = env.executable_path.parent
                            for res_cand in [parent / "resources", parent.parent / "resources", parent / "lib" / "resources"]:
                                if (res_cand / "ion-dist").is_dir() or res_cand.is_dir():
                                    env.resources_path = res_cand
                                    env.app_dir = parent
                                    break
                    except Exception:
                        pass
                break
    except Exception:
        pass

    # 2. 扫描 Linux 常用安装目录
    if not env.resources_path:
        candidates = [
            Path("/opt/Claude/resources"),
            Path("/opt/claude-desktop/resources"),
            Path("/usr/lib/claude-desktop/resources"),
            Path("/usr/share/claude-desktop/resources"),
            Path("/usr/local/lib/claude-desktop/resources"),
            Path.home() / ".local" / "share" / "Claude" / "resources",
            Path.home() / ".local" / "share" / "claude-desktop" / "resources",
            Path.home() / ".local" / "lib" / "claude-desktop" / "resources",
        ]
        for cand in candidates:
            if cand.is_dir():
                env.resources_path = cand
                env.app_dir = cand.parent
                break

    if env.resources_path and env.resources_path.is_dir():
        env.is_installed = True
        # 尝试读取版本
        pkg_json = env.resources_path / "app" / "package.json"
        if not pkg_json.is_file():
            pkg_json = env.resources_path / "package.json"
        if pkg_json.is_file():
            try:
                with open(pkg_json, "r", encoding="utf-8") as f:
                    data = json.load(f)
                    env.version = data.get("version", "已安装")
            except Exception:
                env.version = "已安装"
        else:
            # 尝试通过 dpkg / pacman 查询
            try:
                out = subprocess.check_output(["dpkg-query", "-W", "-f=${Version}", "claude-desktop"], text=True, stderr=subprocess.DEVNULL)
                if out:
                    env.version = out.strip()
            except Exception:
                env.version = "已安装"

    return env

def check_if_localized(env: ClaudeEnvironment) -> bool:
    """检查当前是否已处于汉化状态"""
    if not env.is_installed or not env.resources_path:
        return False
    zh_json = env.resources_path / "ion-dist" / "i18n" / "zh-CN.json"
    return zh_json.is_file()

def load_translation_pack() -> dict:
    """加载汉化词典包（优先加载云端更新缓存）"""
    cached = DATA_DIR / "translation-pack.json"
    if cached.is_file():
        try:
            with open(cached, "r", encoding="utf-8") as f:
                data = json.load(f)
                if data and "frontend" in data:
                    return data
        except Exception:
            pass

    bundled = get_resource_path("translation-pack.json")
    if bundled.is_file():
        try:
            with open(bundled, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {}

def update_user_config_file(path: Path, locale: str):
    """更新用户端 locale 语言配置文件"""
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        data = {}
        if path.is_file():
            try:
                with open(path, "r", encoding="utf-8") as f:
                    data = json.load(f)
            except Exception:
                data = {}
        data["locale"] = locale
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass

def update_all_claude_configs(locale: str):
    """更新所有可能的 Claude 用户配置目录"""
    config_paths = [
        Path.home() / ".config" / "Claude" / "config.json",
        Path.home() / ".config" / "Claude-3p" / "config.json",
        Path.home() / ".config" / "claude-desktop" / "config.json",
        Path.home() / ".var" / "app" / "com.anthropic.Claude" / "config" / "Claude" / "config.json",
    ]
    for p in config_paths:
        update_user_config_file(p, locale)

def inject_script_tag(html_path: Path, backup_dir: Path):
    """向 index.html / frame-shell.html 注入 translator.js 脚本标签"""
    if not html_path.is_file():
        return
    backup_file = backup_dir / f"{html_path.name}.orig"
    if not backup_file.is_file():
        shutil.copy2(html_path, backup_file)

    try:
        content = html_path.read_text(encoding="utf-8")
        if "./translator.js" in content:
            content = content.replace("./translator.js", "/translator.js")
            html_path.write_text(content, encoding="utf-8")
            return
        if "translator.js" in content:
            return
        idx = content.lower().find("</head>")
        if idx != -1:
            modified = content[:idx] + '<script src="/translator.js"></script>' + content[idx:]
            html_path.write_text(modified, encoding="utf-8")
    except Exception:
        pass

def restore_original_file(target_path: Path, backup_dir: Path):
    """从备份目录还原原始文件"""
    backup_file = backup_dir / f"{target_path.name}.orig"
    if backup_file.is_file():
        try:
            shutil.copy2(backup_file, target_path)
        except Exception:
            pass

def perform_apply_localization(env: ClaudeEnvironment) -> None:
    """执行汉化应用"""
    if not env.is_installed or not env.resources_path:
        raise RuntimeError("未检测到 Claude Desktop 安装目录。")

    res = env.resources_path
    if not os.access(res, os.W_OK):
        raise PermissionError(f"权限不足：无法写入 {res}。\n请使用 sudo 运行本程序：sudo ./CCZhAssistant-linux-x64 --apply")

    ion_dist = res / "ion-dist"
    i18n_dir = ion_dist / "i18n"
    dynamic_dir = i18n_dir / "dynamic"
    statsig_dir = i18n_dir / "statsig"
    assets_dir = ion_dist / "assets" / "v1"
    resources_subdir = res / "resources"

    i18n_dir.mkdir(parents=True, exist_ok=True)
    dynamic_dir.mkdir(parents=True, exist_ok=True)
    statsig_dir.mkdir(parents=True, exist_ok=True)
    BACKUP_DIR.mkdir(parents=True, exist_ok=True)

    pack = load_translation_pack()
    if not pack:
        raise RuntimeError("无法加载汉化词典包。")

    # 1. 写入前端 zh-CN.json
    if "frontend" in pack:
        (i18n_dir / "zh-CN.json").write_text(json.dumps(pack["frontend"], ensure_ascii=False), encoding="utf-8")

    # 2. 写入动态模型与思考模式 zh-CN.json
    if "dynamic" in pack:
        (dynamic_dir / "zh-CN.json").write_text(json.dumps(pack["dynamic"], ensure_ascii=False), encoding="utf-8")

    # 3. 写入 statsig zh-CN.json
    if "statsig" in pack:
        (statsig_dir / "zh-CN.json").write_text(json.dumps(pack["statsig"], ensure_ascii=False), encoding="utf-8")

    # 4. 写入桌面级 zh-CN.json 并补充 en-US.json
    if "desktop" in pack and isinstance(pack["desktop"], dict):
        desktop_json = json.dumps(pack["desktop"], ensure_ascii=False)
        (res / "zh-CN.json").write_text(desktop_json, encoding="utf-8")
        if resources_subdir.is_dir():
            (resources_subdir / "zh-CN.json").write_text(desktop_json, encoding="utf-8")

        for en_path in [res / "en-US.json", resources_subdir / "en-US.json"]:
            if en_path.is_file():
                en_backup = BACKUP_DIR / "en-US.json.orig"
                if not en_backup.is_file():
                    shutil.copy2(en_path, en_backup)
                try:
                    with open(en_path, "r", encoding="utf-8") as f:
                        en_dict = json.load(f)
                    en_dict.update(pack["desktop"])
                    with open(en_path, "w", encoding="utf-8") as f:
                        json.dump(en_dict, f, ensure_ascii=False)
                except Exception:
                    pass

    # 5. 部署 translator.js 并注入 HTML
    translator_res = get_resource_path("translator.js")
    if translator_res.is_file():
        translator_code = translator_res.read_text(encoding="utf-8")
        (ion_dist / "translator.js").write_text(translator_code, encoding="utf-8")
        if assets_dir.is_dir():
            (assets_dir / "translator.js").write_text(translator_code, encoding="utf-8")
        inject_script_tag(ion_dist / "index.html", BACKUP_DIR)
        inject_script_tag(ion_dist / "frame-shell.html", BACKUP_DIR)

    # 6. 修补 shared-*.js 语言白名单
    if assets_dir.is_dir():
        for js_file in assets_dir.glob("shared-*.js"):
            try:
                content = js_file.read_text(encoding="utf-8")
                target = '["en-US","de-DE"'
                replacement = '["zh-CN","en-US","de-DE"'
                if target in content and replacement not in content:
                    backup = BACKUP_DIR / f"{js_file.name}.orig"
                    if not backup.is_file():
                        shutil.copy2(js_file, backup)
                    patched = content.replace(target, replacement)
                    js_file.write_text(patched, encoding="utf-8")
                    break
            except Exception:
                pass

    # 7. 更新用户配置文件
    update_all_claude_configs("zh-CN")

def perform_restore_official(env: ClaudeEnvironment) -> None:
    """还原官方英文原版"""
    if not env.is_installed or not env.resources_path:
        return

    res = env.resources_path
    if not os.access(res, os.W_OK):
        raise PermissionError(f"权限不足：无法修改 {res}。\n请使用 sudo 运行本程序：sudo ./CCZhAssistant-linux-x64 --restore")

    ion_dist = res / "ion-dist"
    i18n_dir = ion_dist / "i18n"
    assets_dir = ion_dist / "assets" / "v1"
    resources_subdir = res / "resources"

    # 1. 删除汉化生成文件
    for p in [
        i18n_dir / "zh-CN.json",
        i18n_dir / "dynamic" / "zh-CN.json",
        i18n_dir / "statsig" / "zh-CN.json",
        res / "zh-CN.json",
        resources_subdir / "zh-CN.json",
        ion_dist / "translator.js",
        assets_dir / "translator.js",
    ]:
        if p.is_file():
            try:
                p.unlink()
            except Exception:
                pass

    # 2. 从备份还原 shared-*.js
    if assets_dir.is_dir() and BACKUP_DIR.is_dir():
        for b_file in BACKUP_DIR.glob("shared-*.js.orig"):
            orig_name = b_file.stem  # 去掉 .orig
            target = assets_dir / orig_name
            if target.is_file():
                try:
                    shutil.copy2(b_file, target)
                except Exception:
                    pass

    # 3. 还原 en-US.json, index.html, frame-shell.html
    restore_original_file(res / "en-US.json", BACKUP_DIR)
    restore_original_file(resources_subdir / "en-US.json", BACKUP_DIR)
    restore_original_file(ion_dist / "index.html", BACKUP_DIR)
    restore_original_file(ion_dist / "frame-shell.html", BACKUP_DIR)

    # 4. 重置用户配置语言为 en-US
    update_all_claude_configs("en-US")

def restart_claude(env: ClaudeEnvironment) -> bool:
    """重启 Claude 客户端"""
    try:
        subprocess.run(["pkill", "-f", "claude-desktop"], stderr=subprocess.DEVNULL)
        subprocess.run(["pkill", "-x", "claude"], stderr=subprocess.DEVNULL)
    except Exception:
        pass

    time.sleep(0.8)

    # 启动
    try:
        if env.executable_path and env.executable_path.is_file():
            subprocess.Popen([str(env.executable_path)], start_new_session=True)
            return True
        for cmd in [["gtk-launch", "claude-desktop"], ["claude-desktop"], ["claude"]]:
            try:
                subprocess.Popen(cmd, start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                return True
            except FileNotFoundError:
                continue
    except Exception:
        pass
    return False

def check_remote_manifest() -> dict:
    """检查云端最新词典版本"""
    req = urllib.request.Request(MANIFEST_URL, headers={"User-Agent": f"CCZhAssistant-Linux/{APP_VERSION}"})
    with urllib.request.urlopen(req, timeout=5) as resp:
        return json.loads(resp.read().decode("utf-8"))

def sync_cloud_pack() -> bool:
    """下载并同步云端词典包"""
    try:
        manifest = check_remote_manifest()
        remote_ver = manifest.get("version", "")
        download_url = manifest.get("downloadUrl") or manifest.get("packUrl")
        if not download_url:
            return False

        DATA_DIR.mkdir(parents=True, exist_ok=True)
        cached_file = DATA_DIR / "translation-pack.json"
        
        req = urllib.request.Request(download_url, headers={"User-Agent": f"CCZhAssistant-Linux/{APP_VERSION}"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = resp.read()
            cached_file.write_bytes(data)

        # 保存版本信息
        ver_file = DATA_DIR / "version.txt"
        ver_file.write_text(remote_ver, encoding="utf-8")
        return True
    except Exception:
        return False

def set_autostart(enable: bool) -> None:
    """设置 Linux XDG 开机启动"""
    if enable:
        AUTOSTART_FILE.parent.mkdir(parents=True, exist_ok=True)
        exec_path = sys.executable if not hasattr(sys, "_MEIPASS") else sys.argv[0]
        icon_path = get_resource_path("assistant-icon.png")
        content = f"""[Desktop Entry]
Type=Application
Version=1.0
Name=Claude Code 中文助手
Comment=Claude Desktop / Claude Code 界面汉化伴侣
Exec="{exec_path}"
Icon={icon_path}
Terminal=false
Categories=Utility;
"""
        AUTOSTART_FILE.write_text(content, encoding="utf-8")
    else:
        if AUTOSTART_FILE.is_file():
            AUTOSTART_FILE.unlink()

def is_autostart_enabled() -> bool:
    return AUTOSTART_FILE.is_file()

# ==========================================
# GUI 界面模式 (基于 Tkinter，轻量无外部依赖)
# ==========================================

def run_gui():
    try:
        import tkinter as tk
        from tkinter import ttk, messagebox
    except ImportError:
        print("[!] 当前系统未安装 tkinter 图形库，自动切换为终端命令行模式。")
        print("[*] 提示：在 Ubuntu/Debian 上可通过 'sudo apt install python3-tk' 安装图形支持。")
        run_interactive_cli()
        return

    # 检测是否存在显示服务 (X11 / Wayland)
    if not os.environ.get("DISPLAY") and not os.environ.get("WAYLAND_DISPLAY"):
        print("[!] 未检测到图形桌面环境 (DISPLAY/WAYLAND_DISPLAY 未设置)，自动切换为终端命令行模式。")
        run_interactive_cli()
        return

    root = tk.Tk()
    root.title(APP_NAME)
    root.geometry("500x550")
    root.resizable(False, False)
    root.configure(bg="#ECEFF2")

    # 居中窗口
    root.update_idletasks()
    x = (root.winfo_screenwidth() - 500) // 2
    y = (root.winfo_screenheight() - 550) // 2
    root.geometry(f"500x550+{x}+{y}")

    # 顶层状态
    env = detect_claude_environment()
    localized = check_if_localized(env)

    # 顶部图标与标题
    top_frame = tk.Frame(root, bg="#ECEFF2")
    top_frame.pack(side="top", fill="x", pady=(40, 0))

    icon_img = None
    icon_path = get_resource_path("assistant-icon.png")
    if icon_path.is_file():
        try:
            icon_img = tk.PhotoImage(file=str(icon_path))
            icon_label = tk.Label(top_frame, image=icon_img, bg="#ECEFF2")
            icon_label.image = icon_img
            icon_label.pack()
        except Exception:
            pass

    title_label = tk.Label(top_frame, text="Welcome to Claude", font=("Helvetica", 20, "bold"), fg="#4C4F69", bg="#ECEFF2")
    title_label.pack(pady=(16, 0))

    # 居中主卡片
    card = tk.Frame(root, bg="#ECEFF2", bd=1, relief="solid", padx=24, pady=18)
    card.place(relx=0.5, rely=0.52, anchor="center", width=344, height=175)

    card_title = tk.Label(card, text=f"Claude Code 汉化助手 v{APP_VERSION}", font=("Helvetica", 11, "bold"), fg="#4C4F69", bg="#ECEFF2")
    card_title.pack(pady=(4, 12))

    # 主动作胶囊按钮
    btn_color = "#2EA043" if localized else "#D97757"
    btn_text = "✓  汉化已生效" if localized else "立即应用汉化"

    action_btn = tk.Button(
        card,
        text=btn_text,
        font=("Helvetica", 12, "bold"),
        bg=btn_color,
        fg="white",
        activebackground="#C26547",
        activeforeground="white",
        relief="flat",
        cursor="hand2",
        bd=0,
        height=2
    )
    action_btn.pack(fill="x", padx=10, pady=(0, 12))

    # 选项复选框
    chk_frame = tk.Frame(card, bg="#ECEFF2")
    chk_frame.pack(fill="x")

    auto_update_var = tk.BooleanVar(value=True)
    autostart_var = tk.BooleanVar(value=is_autostart_enabled())

    def on_autostart_toggle():
        set_autostart(autostart_var.get())

    c1 = tk.Checkbutton(chk_frame, text="自动更新", variable=auto_update_var, font=("Helvetica", 10), bg="#ECEFF2", activebackground="#ECEFF2")
    c1.pack(side="left", expand=True)

    c2 = tk.Checkbutton(chk_frame, text="开机启动", variable=autostart_var, command=on_autostart_toggle, font=("Helvetica", 10), bg="#ECEFF2", activebackground="#ECEFF2")
    c2.pack(side="right", expand=True)

    # 卡片下方信息栏
    info_var = tk.StringVar()
    detail_var = tk.StringVar()

    def refresh_ui_state():
        nonlocal env, localized
        env = detect_claude_environment()
        localized = check_if_localized(env)
        
        status_text = "运行中" if env.is_running else "未启动"
        ver_text = env.version.replace("v", "")
        info_var.set(f"Claude v{ver_text}  ·  已汉化 32,606 条  ·  {status_text}")
        
        if not env.is_installed:
            action_btn.config(text="未检测到 Claude", bg="#9CA3AF", state="disabled")
            detail_var.set("未在 /opt 或 ~/.local 等常见路径中找到 Claude Desktop")
        else:
            action_btn.config(state="normal")
            if localized:
                action_btn.config(text="✓  汉化已生效", bg="#2EA043")
            else:
                action_btn.config(text="立即应用汉化", bg="#D97757")

    def on_action_click():
        nonlocal env, localized
        if not env.is_installed:
            return
        if localized:
            try:
                perform_restore_official(env)
                detail_var.set("已恢复官方英文原版！")
                refresh_ui_state()
                if env.is_running and messagebox.askyesno("恢复完成", "已恢复官方原版，是否立即重启 Claude 刷新界面？"):
                    restart_claude(env)
            except Exception as e:
                detail_var.set(f"恢复失败: {e}")
                messagebox.showerror("错误", str(e))
        else:
            try:
                if auto_update_var.get():
                    sync_cloud_pack()
                perform_apply_localization(env)
                detail_var.set("中文汉化包已成功应用！")
                refresh_ui_state()
                if env.is_running and messagebox.askyesno("汉化成功", "中文汉化包已应用生效！是否立即重启 Claude 刷新界面？"):
                    restart_claude(env)
            except Exception as e:
                detail_var.set(f"应用失败: {e}")
                messagebox.showerror("错误", str(e))

    action_btn.config(command=on_action_click)

    info_label = tk.Label(root, textvariable=info_var, font=("Helvetica", 10), fg="#676B81", bg="#ECEFF2")
    info_label.place(relx=0.5, rely=0.74, anchor="center")

    # 底部操作按钮
    btn_frame = tk.Frame(root, bg="#ECEFF2")
    btn_frame.place(relx=0.5, rely=0.83, anchor="center")

    restart_btn = tk.Button(
        btn_frame,
        text="重启 Claude",
        font=("Helvetica", 9),
        bg="white",
        fg="#4C4F69",
        bd=1,
        relief="solid",
        padx=12,
        pady=4,
        cursor="hand2",
        command=lambda: (restart_claude(env), detail_var.set("已发送重启指令。"))
    )
    restart_btn.pack(side="left", padx=8)

    def open_data_dir():
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        try:
            subprocess.Popen(["xdg-open", str(DATA_DIR)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except Exception:
            pass

    dir_btn = tk.Button(
        btn_frame,
        text="配置目录",
        font=("Helvetica", 9),
        bg="white",
        fg="#4C4F69",
        bd=1,
        relief="solid",
        padx=12,
        pady=4,
        cursor="hand2",
        command=open_data_dir
    )
    dir_btn.pack(side="left", padx=8)

    detail_label = tk.Label(root, textvariable=detail_var, font=("Helvetica", 9), fg="#676B81", bg="#ECEFF2")
    detail_label.place(relx=0.5, rely=0.91, anchor="center")

    refresh_ui_state()
    root.mainloop()

# ==========================================
# CLI 命令行 / 交互模式 (无图形环境自适应)
# ==========================================

def run_interactive_cli():
    print(f"\n============================================")
    print(f"       {APP_NAME} v{APP_VERSION} (Linux)")
    print(f"============================================")
    env = detect_claude_environment()
    localized = check_if_localized(env)
    print(f" * Claude 安装状态: {'已检测' if env.is_installed else '未检测到'}")
    if env.is_installed:
        print(f" * 资源路径: {env.resources_path}")
        print(f" * 版本: {env.version}")
        print(f" * 当前汉化状态: {'[✓] 汉化已生效' if localized else '[ ] 官方原版'}")
        print(f" * 运行状态: {'运行中' if env.is_running else '未启动'}")
    print("--------------------------------------------")
    print(" [1] 立即应用汉化")
    print(" [2] 恢复官方英文原版")
    print(" [3] 重启 Claude 客户端")
    print(" [4] 检查并同步云端词典")
    print(" [0] 退出")
    print("--------------------------------------------")
    try:
        choice = input("请输入选项编号 [0-4]: ").strip()
    except (EOFError, KeyboardInterrupt):
        print("\n已退出。")
        return

    if choice == "1":
        cli_apply(env)
    elif choice == "2":
        cli_restore(env)
    elif choice == "3":
        restart_claude(env)
        print("[+] 已触发 Claude 重启。")
    elif choice == "4":
        print("[*] 正在同步云端词典...")
        if sync_cloud_pack():
            print("[+] 云端词典同步成功！")
        else:
            print("[-] 同步失败，请检查网络连接。")
    elif choice == "0":
        print("已退出。")
    else:
        print("无效选项。")

def cli_apply(env: ClaudeEnvironment):
    if not env.is_installed:
        print("[-] 未检测到 Claude 安装目录，请确认 Claude Desktop 已正确安装。")
        sys.exit(1)
    print("[*] 正在应用全量中文语言包...")
    try:
        perform_apply_localization(env)
        print("[+] 汉化包已成功部署并生效！")
        if env.is_running:
            print("[*] 检测到 Claude 正在运行，正在为您自动重启以刷新界面...")
            restart_claude(env)
            print("[+] Claude 已重启完成。")
    except PermissionError as pe:
        print(f"[-] {pe}")
        sys.exit(1)
    except Exception as e:
        print(f"[-] 应用汉化失败: {e}")
        sys.exit(1)

def cli_restore(env: ClaudeEnvironment):
    if not env.is_installed:
        print("[-] 未检测到 Claude 安装目录。")
        sys.exit(1)
    print("[*] 正在恢复官方英文原版...")
    try:
        perform_restore_official(env)
        print("[+] 已成功恢复为官方英文原版配置与文件！")
        if env.is_running:
            restart_claude(env)
            print("[+] Claude 已重启完成。")
    except PermissionError as pe:
        print(f"[-] {pe}")
        sys.exit(1)
    except Exception as e:
        print(f"[-] 恢复原版失败: {e}")
        sys.exit(1)

def cli_status(env: ClaudeEnvironment):
    localized = check_if_localized(env)
    status_data = {
        "appName": APP_NAME,
        "version": APP_VERSION,
        "isInstalled": env.is_installed,
        "isLocalized": localized,
        "isRunning": env.is_running,
        "resourcesPath": str(env.resources_path) if env.resources_path else None,
        "claudeVersion": env.version,
    }
    print(json.dumps(status_data, ensure_ascii=False, indent=2))

def main():
    parser = argparse.ArgumentParser(description=f"{APP_NAME} v{APP_VERSION} (Linux 通用版)")
    parser.add_argument("--apply", action="store_true", help="一键应用中文汉化包")
    parser.add_argument("--restore", action="store_true", help="一键恢复官方英文原版")
    parser.add_argument("--restart", action="store_true", help="重启 Claude Desktop 客户端")
    parser.add_argument("--sync", action="store_true", help="检查并同步最新云端汉化包")
    parser.add_argument("--status", action="store_true", help="以 JSON 格式输出当前安装与汉化状态")
    parser.add_argument("--cli", action="store_true", help="强制进入命令行终端交互模式")

    args = parser.parse_args()
    env = detect_claude_environment()

    if args.status:
        cli_status(env)
    elif args.apply:
        cli_apply(env)
    elif args.restore:
        cli_restore(env)
    elif args.restart:
        restart_claude(env)
    elif args.sync:
        if sync_cloud_pack():
            print("[+] 云端词典同步成功！")
        else:
            print("[-] 同步失败。")
            sys.exit(1)
    elif args.cli:
        run_interactive_cli()
    else:
        # 默认模式：优先启动 GUI，无显示服务或缺少 Tkinter 时自动回退至终端交互
        run_gui()

if __name__ == "__main__":
    main()
