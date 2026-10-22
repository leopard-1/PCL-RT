# PCL-RebuilderTool

> **独立的第三方工具，不是 PCL 官方发布，与PCL官方完全无关。**
> 它不重编译启动器、不修改微软登录、不触碰正版验证与授权逻辑，只对**你自己持有的**官方 PCL 二进制做**一处**功能开关的还原：让 Minecraft 下载入口重新可用。

- 目标版本：**Plain Craft Launcher 2.99.x.x**（如2.99.0.0官方正式版，`5,980,160` 字节）
- 工具形态：单文件 `PCL-Rebuilder.exe`（dnlib 已内嵌，无需装 .NET SDK）+ 一个 `.cmd` 包装
- 不依赖网络，不写注册表，不改系统设置

---

## 它到底改了什么

限制在官方 exe 里是**编译期折叠**进去的（`RestrictMcDownload` 是 `Const`），所以没有"开关"可以直接翻。本工具只处理 3 个真实闸门，全部位于 Minecraft 下载模块：

| # | 位置 | 官方 exe 里的实现 | 本工具的处理 |
|---|------|-------------------|--------------|
| **P1** | `PCL.ModModpack::ModpackInstall(string,string,string)` | 结尾的 `throw` 让后面的 `Select Case PackType` 全部不可达，**7 路分派被整段删除**（`InstallPack*` 实现本身仍在） | 复用 `throw` 那三条指令的位置，重新发射 `ldloc PackType; switch(...)` 分派到 7 个 `InstallPack*` |
| **P2** | `PCL.PageDownloadInstall::LoaderInit` | 版本列表加载器被替换成 `New LoaderTask("关闭下载", Sub() Throw …)`；`Else` 分支被整段删除 | 把这一整段构造替换成 `ldsfld PCL.ModDownload::DlClientListLoader`，即还原成"未限制"时的第 5 个实参 |
| **P3** | `PCL.PageDownloadModify::LoadInstanceFinished`（共 2 处） | `CardMinecraft.Visibility = If(RestrictMcDownload, Collapsed, Visible)` 被折叠成常量 `Collapsed` | `ldc.i4.2`（Collapsed）→ `ldc.i4.0`（Visible） |
| **P4** | `PCL.ModModpack::ModpackInstall()`（无参，「导入整合包」入口） | 方法体被编译器整段删除，只剩 `Hint(限制文案); ret`（19 字节），选文件/起线程的代码**已不存在** | 注入两个静态辅助方法（`PCLRebuild_ImportPack` / `_Pending`），把入口改为调用它们：弹 `Dialogs.SelectFile` → 取第一个结果 → 后台线程跑 `ModpackInstall(File)`，异常转成提示 |

**没有动的部分**：微软登录 6 步（设备码 → XSTS → `login_with_xbox` …）、客户端 ID、正版购买校验、授权与更新逻辑、界面配色、其余全部 20 万行 IL —— 一律原样保留，由 dnlib 按原样重写。

> 顺带说明：官方 exe 自带它自己可用的客户端 ID 与 v2.0 登录流程，所以这条路线**完全不需要碰登录**，比"重编译一个绕过版"更干净。

---

## 用法

### 拖拽（最省事）

把 `Plain Craft Launcher 2.exe` 拖到 `PCL-Rebuilder.cmd` 上。

### 命令行

```bat
PCL-Rebuilder.exe "D:\PCL\Plain Craft Launcher 2.exe"
PCL-Rebuilder.exe "D:\PCL\Plain Craft Launcher 2.exe" --dry-run     报告将要改什么，不动文件
PCL-Rebuilder.exe "D:\PCL\Plain Craft Launcher 2.exe" --restore     从 *.orig.exe 还原
PCL-Rebuilder.exe "D:\PCL\Plain Craft Launcher 2.exe" --force       跳过版本检查（换版本时自担风险）
```

### 行为约定

1. **先校验身份**：拒绝把工具指向它自己；再用 `PCL.ModMain` / `PCL.ModSecret` 两个类型判断"这是不是 PCL"，不是就直接退出。
2. **再校验版本**：读文件版本，必须是 `2.99.x`，否则退出（除非 `--force`）。
3. **先备份**：首次运行在同目录生成 `Plain Craft Launcher 2.orig.exe`（已存在则不覆盖）。
4. **逐项报告**：每个补丁点打印 `[OK]` / `[SKIP]`，并写一份 `*.rebuilder.log`。
5. **幂等**：已打过的补丁会被识别成 `[SKIP] already patched`，可反复运行。
6. **失败即回退**：写入失败时原文件保持不动（先写临时文件，成功才替换）。
7. **可还原**：`--restore` 把 `*.orig.exe` 复制回去。

退出码：`0` 成功 / `2` 参数错误或把工具指向了自己 / `3` 不是 PCL 或版本不符 / `4` 没有匹配到补丁点 / `5` 写入失败。

### 常见误用

**`version: 0.0.0.0` 然后 `[FAIL] that is PCL-Rebuilder itself`**
—— 拖进来的是 `PCL-Rebuilder.exe`（工具本身），不是要打补丁的启动器。要处理的是
`Plain Craft Launcher 2.exe`（约 5,980,160 字节，官方 PCL 正式版）。这种情况工具**不会写任何文件**，
连备份都不会生成，可以放心重试。

---

## 已验证

| 项目 | 方法 | 结果 |
|------|------|------|
| 三个补丁点全部命中 | 在官方 2.99.0.0 上实跑 | ✅ P1 + P2 + P3×2 = 4 处 |
| P2 还原正确 | 反汇编补丁后的 exe，检查 `LoaderInit` | ✅ 栈序为 `LoadMinecraft, PanLoad, PanAllBack, null, **DlClientListLoader**, Action` —— 与"未限制"分支一致 |
| P1 生效 | 统计限制文案字面量 | ✅ 5 处 → 4 处（少掉的正是被 nop 的那条） |
| P3 生效 | 反汇编 `LoadInstanceFinished` | ✅ `get_CardMinecraft` 后由 `ldc.i4.2` 变为 `ldc.i4.0` |
| P4 注入正确 | 反汇编 `ModpackInstall()` / `PCLRebuild_ImportPack` | ✅ 入口变为 `call PCLRebuild_ImportPack; ret`；辅助方法里 `SelectFile(标题, false, null, default(Nullable))` → 取首个结果 → `RunInThread` |
| P4 不影响启动 | 单独启动补丁后的 exe | ✅ 主窗口正常，`HasExited=False` |
| P1 分派生效 | 实机导入整合包 | ✅ 选择框 → 安装流程正常；关键的 IL 修正：离开 try 区必须用 `leave` 而不是 `br` |
| 资源未损坏 | 单独启动补丁后的 exe（目录内**没有**任何 DLL） | ✅ 主窗口正常出现，`Responding=True`，闭源程序集仍能从内嵌资源加载 |
| 幂等 | 连续运行两次 | ✅ 第二次全部 `SKIP`，返回 0，不写文件 |
| 误指向自身 | 把工具路径当参数传入 | ✅ 返回 2，不写文件、不生成备份 |
| 误指向别的程序 | `dnlib.dll` / `notepad.exe` | ✅ 返回 3，分别报"不是 PCL 构建"和"不是托管程序" |
| 体积 | 官方 `5,980,160` → 补丁后 `5,972,992` | ✅ 含 P4 注入的两个方法与一个字段 |

> **已实机验证通过**：下载页版本列表正常、点「导入整合包」弹出文件选择框、选中包后正常进入安装流程。

---

## 已知限制

- **只支持 2.99.0.0**。PCL 每次改动下载模块后，IL 指纹都会变；工具会明确报"没有匹配到补丁点"而不是乱改。
- **P4 是唯一需要"注入"的补丁**：前三个补丁都是原地改指令（尺寸不变），而「导入整合包」的原始代码在官方 exe 里已被彻底删除，只能新增方法。注入的只有 2 个静态方法 + 1 个静态字段，不新增类型、不改任何既有方法的签名。因此补丁后 exe 会比纯 NOP 方案再大一点（`5,972,992` 字节）。
- **不改登录 / 授权**：这是设计约束，不是缺陷。微软登录继续用官方 exe 自带的客户端 ID。
- 补丁后 exe 不再与官方发布文件逐字节一致，**不要把它当成官方版本分发**。

---

## 免责声明

使用该工具对PCL进行补丁后，后续产生的任何bug与错误均与PCL官方，PCL社区无关，请自行解决！若有顾虑请谨慎使用。

---

## 目录

```
PCL-Rebuilder/
├─ PCL-Rebuilder.exe        单文件工具（dnlib 内嵌）
├─ PCL-Rebuilder.cmd        拖拽 / 命令行包装（纯 ASCII，任何代码页安全）
├─ README.md
└─ src/
   ├─ PCL-Rebuilder.cs      补丁逻辑（三个 site 各自独立、带指纹校验）
   └─ Bootstrap.cs          dnlib 内存加载（单文件分发用）
```

自行编译：

```bat
csc /target:exe /platform:anycpu /optimize+ /out:PCL-Rebuilder.exe ^
    /r:dnlib.dll /resource:dnlib.dll,dnlib.dll ^
    src\PCL-Rebuilder.cs src\Bootstrap.cs
```

依赖：[dnlib](https://github.com/0xd4d/dnlib)（MIT）。

大部分编写工作由AI工具完成

## 许可证

该工具使用 ```MIT``` 许可证
