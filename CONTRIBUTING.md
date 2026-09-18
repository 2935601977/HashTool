# 参与开发

先说结论：这是个刻意做「小」的工具，**只有两条硬约束**，其余都好商量。

## 两条硬约束

1. **不引入任何第三方依赖**。只用 Windows 自带的东西（.NET Framework 4.x 的 BCL、WinForms、
   `System.Security.Cryptography`），编译只用系统自带的 `csc.exe`。
   不引 NuGet 包，不要求用户装 .NET SDK / Visual Studio / 运行库。
2. **源码保持 C# 5 语法**。因为系统自带的 `csc.exe`（`Framework64\v4.0.30319`，C# 5 编译器）
   要能直接编。具体就是不要用：
   - 字符串插值 `$"..."`（用 `+` 或 `string.Format`）
   - null 条件运算符 `?.`、`??=`、`out var`、元组、模式匹配
   - 局部函数、表达式体成员（`=> ...`）、自动属性初始化器
   - `nameof`、`using static`、`async/await`（本项目的并发用 `Thread` + `ConcurrentQueue`）

   这些新语法在 `dotnet build` / 新版 Roslyn 下当然能编，但那样就丢掉了「不装任何东西」这个卖点。

## 目录结构

```
src/HashTool.cs       全部源码（界面 + 扫描 + 哈希 + JSON + 自检），单文件，约 2400 行
src/app.ico           程序图标
src/app.manifest      高 DPI 感知 + 普通权限运行
tools/make-icon.ps1   重新生成图标（以及 docs/icon.png 预览图）
build.bat             编译（产物 dist/HashTool.exe）
docs/                 README 用的图片
```

## 本地开发流程

```bat
:: 1. 改 src\HashTool.cs
:: 2. 编译（双击 build.bat 也行）
build.bat

:: 3. 自检：哈希已知向量 + 界面构造 + 递归扫描
dist\HashTool.exe --selftest

:: 4. 界面自测：13 项断言，覆盖增量刷新和 Ctrl+C 复制
dist\HashTool.exe --uitest .\ui_out

:: 5. 改了界面的话，重新生成 README 截图
dist\HashTool.exe --screenshot docs\screenshot.png
```

改了行为就顺手加一条 `--uitest` 断言（在 `Program.RunUiTest` 里），这样以后别人改坏会被 CI 拦住。

## 提交 Pull Request 之前

- `build.bat` 编译无警告（本项目把 CS0649 这类"字段声明了没赋值"的警告当错误，防止空引用）
- `--selftest` 和 `--uitest` 都通过
- 动过界面就附上新的 `--screenshot` 截图
- 用中文或英文写提交信息都可以，说清楚"为什么"比"改了什么"更重要

## 发布流程（维护者）

1. 更新 `CHANGELOG.md` 和 `src/HashTool.cs` 里的 `Const.Version`
2. 提交：`git commit -m "release: v1.2.0"`
3. 打 tag 并推送：`git tag v1.2.0 && git push origin main --tags`
4. GitHub Actions 会自动编译、自检、把 `HashTool.exe` 和它的 SHA-256 附到 Release 上
