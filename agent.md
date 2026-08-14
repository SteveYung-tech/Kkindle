# Kkindle 项目 Agent 约定

## Shell 使用约定

- 默认 shell 是 **Windows PowerShell 7**。
- 在编写或运行命令之前，除非用户明确说明 shell 是 Bash、WSL、Git Bash 或其他 shell，否则默认使用 **PowerShell 语法**。
- **默认禁止使用 Bash 专用语法**（例如 `&&`、`||`、`export VAR=...`、`VAR=value command`、`$(...)`、`;` 分隔等），改用 PowerShell 等价写法。
- 对包含逗号的参数加引号，例如：`'a,b,c'`。
- JSON 参数优先使用单引号包裹，例如：`'{"ids":[1,2,3]}'`。
- 注意 PowerShell 中的特殊字符：`$`、`;`、反引号（`` ` ``）、引号、反斜杠、带空格的路径以及逗号；需要时使用反引号转义或单引号字符串避免意外解析。
- 在路径可能产生歧义、跨目录操作、读写具体文件、复制移动文件或排查问题时，**优先使用 Windows 绝对路径**，例如：`C:\Users\name\project`。
- 如果只是项目内部命令，且已经明确处于项目根目录，可以使用相对路径。
