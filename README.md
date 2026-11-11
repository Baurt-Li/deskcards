# DeskCards · 悬浮卡片助手

一个 Windows 桌面待办工具。任务以彩色卡片显示，完成后变暗并标记为「已完成」。窗口可置顶，也可以停靠在屏幕左右边缘。

## 下载与使用

从 [最新发布](https://github.com/Baurt-Li/deskcards/releases/latest) 下载 `DeskCards.exe`，双击运行。

- 左右拖动卡片可循环切换任务。
- 未完成的任务会延续到之后的日期。跨天任务标记完成后，会在完成当天继续显示。
- 向下拖动可预览并展开卡片叠，点任意卡片回到单卡视图。
- 向上拖动可滑出添加卡片，在卡片里输入任务，按回车保存。
- 拖住顶部横条可移动窗口。停靠在屏幕边缘后，卡片会收起并露出一小截卡边。鼠标移到卡边上即可展开。
- 从 Windows 右下角的通知区域可以显示、隐藏或退出卡片助手。图标也可能在 **`^`** 菜单中。

任务和窗口设置保存在本机的 `%LOCALAPPDATA%\DeskCards\tasks.xml`。

## 从源码构建

在 Windows 上打开 PowerShell，进入仓库目录后运行下面的命令。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建使用 Windows 自带的 .NET Framework 4.x 和 WPF，输出为 `dist\DeskCards.exe`。
