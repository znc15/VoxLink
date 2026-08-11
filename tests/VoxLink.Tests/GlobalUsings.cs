// 测试项目的全局导入：Xunit 通过全局 using 声明（csproj `<Using Include>` 不被
// csharp-ls 的 Roslyn 工作区处理，源级 global using 可让 LSP 与构建一致）。
global using Xunit;
