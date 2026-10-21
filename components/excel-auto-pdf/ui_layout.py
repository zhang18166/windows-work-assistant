"""固定日志工作区；详细设置独立显示，不再用整页滚动挤走日志。"""
from __future__ import annotations
import tkinter as tk
from tkinter import ttk, scrolledtext
from PIL import Image, ImageDraw, ImageTk

BG = "#F4F7FA"
CARD = "#FFFFFF"
INK = "#183047"
MUTED = "#53677B"
ACCENT = "#087C66"


class ResponsiveLayout:
    def _configure_window(self):
        self.root.title(f"{self.APP_NAME}  v{self.APP_VERSION}")
        self._window_icon_tk = ImageTk.PhotoImage(self._icon_factory())
        self.root.iconphoto(True, self._window_icon_tk)
        self.root.geometry("860x700")
        # 高于 150% 缩放时同比放大最小尺寸，保证日志至少保留数行。
        minimum_scale = max(1, self.root.winfo_fpixels("1i") / 144)
        self.root.minsize(round(640*minimum_scale), round(540*minimum_scale))
        self.root.configure(background=BG)
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure("TFrame", background=BG)
        style.configure("TButton", font=("Microsoft YaHei UI", 10), padding=(10, 5),
                        background="#F4F7F9", foreground=INK, bordercolor="#CCD6DF")
        style.map("TButton", background=[("active", "#E8F1F5"), ("disabled", "#F7F9FB")],
                  foreground=[("disabled", "#8795A3")])
        style.configure("Accent.TButton", background=ACCENT, foreground="white", bordercolor=ACCENT)
        style.map("Accent.TButton", background=[("active", "#086752"), ("disabled", "#E6ECEF")],
                  foreground=[("disabled", "#8795A3")])
        style.configure("TEntry", font=("Microsoft YaHei UI", 10), fieldbackground=CARD, padding=4)
        style.map("TEntry", fieldbackground=[("disabled", BG)], foreground=[("disabled", MUTED)])
        style.configure("TSpinbox", font=("Microsoft YaHei UI", 10), padding=3)
        # 绘制对勾，替换 clam 的小叉；保持标准 Checkbutton 状态与键盘操作。
        size = max(18, round(self.root.winfo_fpixels("1i") / 96 * 18))
        self._check_images = []
        for selected, disabled in ((False, False), (True, False), (False, True), (True, True)):
            im = Image.new("RGBA", (size, size), CARD)
            draw = ImageDraw.Draw(im)
            color = "#738899" if disabled else ACCENT
            draw.rounded_rectangle((1, 1, size-2, size-2), radius=3,
                                   fill=color if selected else CARD, outline=color, width=2)
            if selected:
                draw.line([(size*.23, size*.5), (size*.43, size*.7), (size*.78, size*.29)],
                          fill="white", width=max(2, round(size*.12)), joint="curve")
            self._check_images.append(ImageTk.PhotoImage(im, master=self.root))
        style.element_create("ClearCheck.indicator", "image", self._check_images[0],
            ("disabled", "selected", self._check_images[3]),
            ("selected", self._check_images[1]), ("disabled", self._check_images[2]), sticky="w")
        style.layout("Card.TCheckbutton", [("Checkbutton.padding", {"sticky":"nswe", "children":[
            ("ClearCheck.indicator", {"side":"left", "sticky":"w"}),
            ("Checkbutton.focus", {"side":"left", "sticky":"w", "children":[
                ("Checkbutton.label", {"sticky":"nswe"})]})]})])
        style.configure("Card.TCheckbutton", font=("Microsoft YaHei UI", 10), background=CARD,
                        foreground=INK, padding=(0, 6))
        style.map("Card.TCheckbutton", background=[("active", CARD), ("disabled", CARD)],
                  foreground=[("disabled", MUTED)])

    @staticmethod
    def _entry(parent, variable):
        return tk.Entry(parent, textvariable=variable, state="readonly", readonlybackground=CARD,
            fg=INK, relief=tk.FLAT, bd=0, font=("Microsoft YaHei UI", 10), width=1)

    def _build_widgets(self):
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(4, weight=1)
        header = tk.Frame(self.root, bg=BG)
        header.grid(row=0, column=0, sticky="ew", padx=16, pady=(12, 8))
        header.columnconfigure(0, weight=1)
        tk.Label(header, text=self.APP_NAME, bg=BG, fg=INK,
            font=("Microsoft YaHei UI", 15, "bold")).grid(row=0, column=0, sticky="w")
        tk.Label(header, text="v"+self.APP_VERSION, bg=BG, fg=MUTED,
            font=("Microsoft YaHei UI", 9)).grid(row=0, column=1)
        self.notice_message = tk.Label(self.root, textvariable=self.notice_var, bg="#FFF4DC", fg="#805500",
            font=("Microsoft YaHei UI", 9), justify=tk.LEFT, anchor="w", padx=8, pady=4, wraplength=600)
        self.notice_message.grid(row=1, column=0, sticky="ew", padx=16, pady=(0, 6))
        if not self.notice_var.get(): self.notice_message.grid_remove()
        folder = tk.Frame(self.root, bg=CARD, padx=10, pady=8)
        folder.grid(row=2, column=0, sticky="ew", padx=16)
        folder.columnconfigure(0, weight=1)
        self.folder_display = self._entry(folder, self.folder_var)
        self.folder_display.grid(row=0, column=0, columnspan=3, sticky="ew", pady=(0, 5))
        self.settings_summary = tk.StringVar()
        tk.Label(folder, textvariable=self.settings_summary, bg=CARD, fg=MUTED,
            font=("Microsoft YaHei UI", 9), anchor="w").grid(row=1, column=0, sticky="w")
        ttk.Button(folder, text="设置…", command=self._show_settings).grid(row=1, column=1, padx=(8, 6))
        self.open_button = ttk.Button(folder, text="打开目录", command=self.open_folder)
        self.open_button.grid(row=1, column=2)
        middle = tk.Frame(self.root, bg=BG)
        middle.grid(row=3, column=0, sticky="ew", padx=16, pady=8)
        middle.columnconfigure(0, weight=1)
        actions = tk.Frame(middle, bg=BG)
        actions.grid(row=0, column=0, sticky="ew", pady=(0, 7))
        self.start_button = ttk.Button(actions, text="开始监控", command=self.start_monitoring, style="Accent.TButton")
        self.start_button.pack(side=tk.LEFT, padx=(0, 6))
        self.stop_button = ttk.Button(actions, text="停止监控", command=self.stop_monitoring)
        self.stop_button.pack(side=tk.LEFT, padx=(0, 6))
        self.exit_button = ttk.Button(actions, text="退出程序", command=lambda:self.request_exit(confirm=True))
        self.exit_button.pack(side=tk.LEFT)
        status = tk.Frame(middle, bg=CARD, padx=10, pady=7)
        status.grid(row=1, column=0, sticky="ew")
        status.columnconfigure(1, weight=1)
        self.status_label = tk.Label(status, textvariable=self.status_var, bg=CARD,
            fg=self.STATUS_COLORS["已停止"], font=("Microsoft YaHei UI", 11, "bold"), anchor="w")
        self.status_label.grid(row=0, column=0, sticky="w", padx=(0, 14))
        self.detail_message = self._entry(status, self.status_detail_var)
        self.detail_message.grid(row=0, column=1, sticky="ew")
        self.progress_message = self._entry(status, self.progress_var)
        self.progress_message.configure(font=("Microsoft YaHei UI", 9), fg=MUTED)
        self.progress_message.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(4, 0))
        self.logs_panel = tk.Frame(self.root, bg=CARD, padx=10, pady=8)
        self.logs_panel.grid(row=4, column=0, sticky="nsew", padx=16, pady=(0, 8))
        self.logs_panel.columnconfigure(0, weight=1)
        self.logs_panel.rowconfigure(1, weight=1)
        log_header = tk.Frame(self.logs_panel, bg=CARD)
        log_header.grid(row=0, column=0, sticky="ew", pady=(0, 5))
        log_header.columnconfigure(0, weight=1)
        tk.Label(log_header, text="转换日志 · 最新在上", bg=CARD, fg=INK,
            font=("Microsoft YaHei UI", 10, "bold")).grid(row=0, column=0, sticky="w")
        ttk.Button(log_header, text="最新记录", command=self._show_latest_log).grid(row=0, column=1, padx=(0, 6))
        self.log_open_button = ttk.Button(log_header, text="日志目录", command=self.open_log_folder)
        self.log_open_button.grid(row=0, column=2)
        self.log_text = scrolledtext.ScrolledText(self.logs_panel, wrap=tk.WORD, state=tk.DISABLED,
            font=("Microsoft YaHei UI", 9), background="#FBFCFE", foreground=INK,
            relief=tk.FLAT, borderwidth=0, padx=6, pady=6, width=1, height=5)
        self.log_text.grid(row=1, column=0, sticky="nsew")
        self.log_text.tag_configure("timestamp", foreground=ACCENT, font=("Microsoft YaHei UI", 9, "bold"))
        self.last_log_var = tk.StringVar(value="等待日志")
        tk.Label(self.root, textvariable=self.last_log_var, bg=BG, fg=MUTED,
            font=("Microsoft YaHei UI", 9), anchor="w").grid(row=5, column=0, sticky="ew", padx=16, pady=(0, 8))
        self._build_settings()
        for variable in (self.recursive_var, self.startup_var):
            variable.trace_add("write", lambda *args:self._refresh_summary())
        self._refresh_summary()
        self.root.bind("<Configure>", self._on_root_resize, add="+")

    def _build_settings(self):
        self.settings_window = tk.Toplevel(self.root)
        self.settings_window.withdraw()
        self.settings_window.title("监控设置")
        self.settings_window.configure(bg=CARD)
        self.settings_window.transient(self.root)
        self.settings_window.protocol("WM_DELETE_WINDOW", self.settings_window.withdraw)
        panel = tk.Frame(self.settings_window, bg=CARD, padx=18, pady=16)
        panel.pack(fill=tk.BOTH, expand=True)
        panel.columnconfigure(0, weight=1)
        tk.Label(panel, text="监控文件夹", bg=CARD, fg=INK,
            font=("Microsoft YaHei UI", 11, "bold")).grid(row=0, column=0, sticky="w", pady=(0, 8))
        self.folder_entry = ttk.Entry(panel, textvariable=self.folder_var, width=1)
        self.folder_entry.grid(row=1, column=0, sticky="ew", padx=(0, 8))
        self.folder_entry.bind("<FocusOut>", lambda event:self.save_current_config())
        self.choose_button = ttk.Button(panel, text="选择文件夹", command=self.choose_folder)
        self.choose_button.grid(row=1, column=1)
        self.recursive_check = ttk.Checkbutton(panel, text="  包含所有子文件夹", variable=self.recursive_var,
            command=self.save_current_config, style="Card.TCheckbutton")
        self.recursive_check.grid(row=2, column=0, columnspan=2, sticky="w", pady=(10, 0))
        self.startup_check = ttk.Checkbutton(panel, text="  开机启动并自动监控（隐藏到托盘）", variable=self.startup_var,
            command=self.apply_startup_setting, style="Card.TCheckbutton")
        self.startup_check.grid(row=3, column=0, columnspan=2, sticky="w")
        timeout = tk.Frame(panel, bg=CARD)
        timeout.grid(row=4, column=0, columnspan=2, sticky="w", pady=10)
        tk.Label(timeout, text="单文件超时", bg=CARD, fg=INK, font=("Microsoft YaHei UI", 10)).pack(side=tk.LEFT)
        self.timeout_spinbox = ttk.Spinbox(timeout, from_=30, to=3600, increment=30, width=6,
            textvariable=self.task_timeout_var, command=self.save_current_config)
        self.timeout_spinbox.pack(side=tk.LEFT, padx=(10, 6))
        self.timeout_spinbox.bind("<FocusOut>", lambda event:self.save_current_config())
        tk.Label(timeout, text="秒，超时后继续下一项", bg=CARD, fg=MUTED,
            font=("Microsoft YaHei UI", 9)).pack(side=tk.LEFT)
        tk.Label(panel, text="监控期间，目录、子文件夹和超时设置不可修改。\n设置即时保存；关闭此窗口后继续监控。",
            bg=CARD, fg=MUTED, justify=tk.LEFT, font=("Microsoft YaHei UI", 9)).grid(
                row=5, column=0, columnspan=2, sticky="w", pady=(0, 12))
        ttk.Button(panel, text="完成", command=self.settings_window.withdraw).grid(row=6, column=1, sticky="e")

    def _show_settings(self):
        self.settings_window.update_idletasks()
        width = max(640, self.settings_window.winfo_reqwidth())
        height = self.settings_window.winfo_reqheight()
        self.settings_window.geometry(f"{width}x{height}")
        self.settings_window.minsize(width, height)
        self.settings_window.deiconify()
        self.settings_window.lift()

    def _refresh_summary(self):
        self.settings_summary.set("子目录："+("含" if self.recursive_var.get() else "不含")+
            " · 自启："+("开" if self.startup_var.get() else "关"))

    def _show_latest_log(self):
        self.log_text.yview_moveto(0)

    def _on_root_resize(self, event):
        if event.widget is self.root:
            self.notice_message.configure(wraplength=max(100, event.width-48))

    def _set_notice(self, text):
        self.notice_var.set(text)
        if text: self.notice_message.grid()
        else: self.notice_message.grid_remove()
