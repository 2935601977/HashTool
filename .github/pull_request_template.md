## 这个 PR 做了什么

<!-- 一两句话说明白 -->

## 类型

- [ ] 修 Bug
- [ ] 新功能
- [ ] 文档 / 注释
- [ ] 重构（行为不变）

## 自查清单

- [ ] 我跑过 `build.bat`，编译没有警告（本项目把 CS0649 这类警告当错误）
- [ ] 我跑过 `dist\HashTool.exe --selftest`，输出 `SELFTEST OK`
- [ ] 我跑过 `dist\HashTool.exe --uitest .\ui_out`，输出 `UITEST OK`（13 项断言全 PASS）
- [ ] 新增的代码仍然是 **C# 5 语法**（没有字符串插值 / `?.` / `out var` / 局部函数）
- [ ] 没有引入任何第三方依赖
- [ ] 改了界面的话，我附上了 `--screenshot` 生成的新截图

## 相关 Issue

<!-- 例如 Closes #12 -->
