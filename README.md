# PDFReader

Windows 桌面 PDF 阅读、OCR、书签、TTS 和标注工具。项目使用 Avalonia/.NET 构建，页面渲染由 MuPDFCore 完成；OCR 与 PDF 标注读写通过本地 Python worker 执行。

## 功能

- 多 PDF 工作区、页码跳转、缩放、连续滚动阅读和低倍率缩略图缓存。
- 连续阅读使用虚拟化多页列表：页面先显示占位或缓存预览，再后台回填高清图，适合大型 PDF 的快速滚动。
- 连续阅读可直接显示当前页已保存的 OCR 框，并通过喇叭按钮播放或按设置自动生成音频。
- 框选页面区域，使用 PP-OCR ONNX 识别中文等文本；支持 CPU 与 DirectML 自动选择。
- OCR 结果需用户确认后保存，可挂载到多级书签并生成/播放 TTS 音频。
- 书签树支持拖拽调整层级、重命名、跳转、删除和撤回删除。
- PDF 标注支持选择、文本框、直线、自由绘制、方框、高亮与轨迹橡皮擦。修改先缓存在会话中，点击“保存标注”后才增量写回 PDF。
- 文件记录支持归档、恢复与彻底移除；彻底移除会删除关联的 OCR、书签、音频和截图资源。
- “全量导出 PDF”将标准书签写入 PDF Outline，将音频作为 PDF Embedded File 附件，并保存 OCR 元数据清单。重新导入新版导出文件可恢复书签、OCR 与音频关联。

## 运行要求

- Windows x64
- .NET SDK 10（开发时）
- Python 3.10+ 与本地虚拟环境 `.venv`
- OCR 模型：`ocr_model/det.onnx`、`ocr_model/rec.onnx` 和 `inference.yml` 或 `ppocr_keys_v1.txt`
- 可用的 OpenAI Compatible TTS 服务（仅生成语音时需要）

安装 Python 依赖：

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\Scripts\requirements-ocr.txt
```

可通过 `PDFREADER_PYTHON` 指向其他 Python 解释器。OCR 设备环境变量为 `PDFREADER_OCR_DEVICE`：`auto`（默认，优先 DirectML）、`directml` 或 `cpu`。

## 开发运行

```powershell
dotnet restore
dotnet run
```

仅验证构建：

```powershell
dotnet build .\PDFReader.csproj -c Release --no-restore
```

## 数据与导入导出

运行数据位于应用目录下的 `user_data/`：

```text
user_data/
├─ reader.db                 SQLite 数据库
├─ settings.json             设置，TTS API Key 使用 DPAPI 加密
├─ cache/                    页面缩略图缓存
└─ resource/
   ├─ image/                 可选 OCR 截图缓存
   └─ voice/                 TTS 音频
```

首次导入普通 PDF 时，若本地没有书签，应用会读取标准 PDF Outline 并创建书签树。导入由 PDFReader 全量导出的新版文件时，会优先检测 PDF 内嵌的 `PDFReader-metadata.json` 并恢复应用元数据；普通阅读器仍可使用其中的标准书签和提取音频附件。

启动后会在后台清理无效文档关联、失效书签关联的 OCR 与无主音频记录，并删除这些记录引用的资源文件。不会扫描资源目录删除无法确认归属的文件。

## 设置

设置窗口可调整：

- 页面缩略图开关（默认开启）
- OCR 截图缓存开关与输出目录
- TTS 音频目录
- TTS Base URL、API Key、Model Type
- Voice Model 名称与 `voice_id` 键值对
- OCR 喇叭按钮是否自动生成缺失音频（默认关闭）

## 发布与安装包

生成带 .NET 运行时的发布目录：

```powershell
dotnet publish .\PDFReader.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o .\publish\win-x64
```

使用当前 `.venv` 一并打包，并可生成 Inno Setup 安装包：

```powershell
.\Scripts\build-release.ps1 -BuildInstaller
```

构建 ARM64 发布目录或安装包时，使用 ARM64 Python 创建的 `.venv`：

```powershell
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64 -BuildInstaller
```

安装包卸载时会询问是否保留 `user_data`；页面缩略图缓存会被单独删除。

## 说明

- TTS API 按 OpenAI Compatible `/audio/speech` 端点请求，并使用 MP3 输出。
- PDF 注释以 PyMuPDF 增量写入。复杂或非标准第三方标注可以被选择和删除，但未必能被完全还原为可编辑的同类型对象。
- 详细设计、数据库字段、环境变量和验证清单见 [DEVELOPMENT.md](DEVELOPMENT.md)。
