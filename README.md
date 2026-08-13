# PDFReader

Windows 桌面 PDF 阅读、OCR、书签、TTS 和标注工具。项目使用 Avalonia/.NET 构建，页面渲染由 MuPDFCore 完成；OCR 和 PDF 标注读写均通过本地 Python worker 执行，底层使用 PyMuPDF。

## 功能

- 多 PDF 工作区、页码跳转、可编辑百分比缩放、`Ctrl + 滚轮` 缩放、连续滚动阅读和低倍率缩略图缓存。
- 连续阅读使用虚拟化多页列表：页面先显示占位或缓存预览，再后台回填高清图，适合大型 PDF 的快速滚动。
- 连续阅读可直接显示当前页已保存的 OCR 框，并通过喇叭按钮播放或按设置自动生成音频。
- 框选页面区域，使用 PP-OCRv5 server ONNX 识别中文、繁体中文、英文和日文等文本；支持 CPU 与 DirectML 自动选择。
- OCR 截取会进入顺序后台队列，可连续框选多个区域；结果需用户确认后保存，并显示识别中、待确认或待挂载状态。
- 顶部“资源”可管理当前 PDF 的 OCR 与音频资源，支持 OCR 标题搜索和已挂载/未挂载筛选；音频始终依附于 OCR，删除 OCR 会联动删除音频和截图。
- 书签树支持拖拽或右键选择父书签调整层级、名称包含搜索定位、跳转、删除和撤回删除。
- PDF 标注支持选择、文本框、直线、自由绘制、方框、高亮与轨迹橡皮擦。修改先缓存在会话中，点击“保存标注”后才增量写回 PDF。
- 文件记录支持归档、恢复与彻底移除；彻底移除会删除关联的 OCR、书签、音频和截图资源。
- “全量导出 PDF”将标准书签写入 PDF Outline，将音频作为 PDF Embedded File 附件，并保存 OCR 元数据清单。重新导入新版导出文件可恢复书签、OCR 与音频关联。
- “文件 > 导出 HTML5 阅读包”生成可离线打开的目录包，按页跳转并懒加载 WebP 页面图，叠加 OCR 框和标题；存在音频的 OCR 可播放并参与本页朗读，没有音频的 OCR 仍会正常导出和显示。

## 运行要求

- Windows x64 或 Windows ARM64
- .NET SDK 10（开发时）
- Python 3.10+；x64 使用 `.venv`，ARM64 使用 `.venv-arm64`
- OCR 模型：`ocr_model/det.onnx`、`ocr_model/rec.onnx` 和 `inference.yml` 或 `ppocr_keys_v1.txt`
- 可用的 OpenAI Compatible TTS 服务（仅生成语音时需要）

安装 Python 依赖：

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\Scripts\requirements-ocr.txt
```

Windows ARM64 使用[项目提供的 ARM64 wheel](https://github.com/Rurityan/windows-arm64-native-deps/tree/main/wheels) 安装 OCR 后端。请使用 ARM64 Python，并将环境创建为 `.venv-arm64`：

```powershell
py -3.11 -m venv .venv-arm64
.\.venv-arm64\Scripts\python.exe -m pip install --no-index --no-deps --find-links .\py-libs\win-arm64 `
    numpy==2.4.6 PyMuPDF==1.28.2 onnxruntime-directml==1.30.0 `
    opencv-python-headless==4.14.0.94 pyclipper==1.4.0
.\.venv-arm64\Scripts\python.exe -m pip install -r .\Scripts\requirements-ocr-arm64.txt
.\.venv-arm64\Scripts\python.exe -m pip install --no-index --no-deps --find-links .\py-libs\win-arm64 -r .\Scripts\requirements-rich-media-arm64.txt
```

ARM64 wheel 包含原生 `cv2`、`pyclipper`、ONNX Runtime DirectML、PyMuPDF、`miniaudio` 和已完成 ARM64 原生构建的 `pikepdf`。OCR 默认使用 DirectML，失败时回退 CPU。

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

“资源”窗口仅管理当前打开 PDF 的 OCR 记录。可单独删除某条 OCR 的全部音频，或删除 OCR 本身及其关联音频、截图；不会创建、导入或保留没有 OCR 关联的独立音频记录。

## 设置

设置窗口可调整：

- 页面缩略图开关（默认开启）
- OCR 截图缓存开关与输出目录
- TTS 音频目录
- TTS Base URL、API Key、Model Type
- Voice Model 名称与 `voice_id` 键值对
- OCR 喇叭按钮是否自动生成缺失音频（默认关闭）
- 本地自动化接口 Token

## 自动化导入接口

本地自动化接口默认关闭；在设置页启用并配置端口后，外部程序通过本地 Token 和 `X-PDFReader-Token` 请求头批量导入 OCR 和已有音频。接口只监听本机，外部程序不应直接写入 `reader.db`。完整字段约定、错误响应和自动化示例见 [REST_API.md](REST_API.md)。

“文件”菜单中的“导出 Adobe Acrobat 富媒体 PDF”会将已有 VOC 音频嵌入 PDF，并在对应 OCR 区域写入标准声音注释，供 Acrobat 识别和播放。x64 和 ARM64 安装包均包含对应架构的 `pikepdf` 与 `miniaudio`；这些库仅在执行导出时加载。ARM64 `pikepdf` wheel 内置 qpdf 12.2.0、zlib 和所需 MSVC 运行库，不需要额外安装 qpdf。

```json
{
  "pdfPath": "D:\\docs\\book.pdf",
  "records": [{
    "page": 12,
    "region": { "x": 120, "y": 240, "width": 860, "height": 180 },
    "captureZoom": 1.0,
    "title": "章节标题",
    "text": "OCR 正文",
    "audioFile": "D:\\automation-output\\page-12-001.mp3"
  }]
}
```

接口会按路径匹配或创建 PDF 记录，将音频复制到应用资源目录，并按页自动挂载到已有书签。没有同页书签的 OCR 保持未挂载状态。

## 发布与安装包

生成带 .NET 运行时的发布目录：

```powershell
dotnet publish .\PDFReader.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o .\publish\win-x64
```

使用当前 `.venv` 打包 x64 版本，并可生成 Inno Setup 安装包：

```powershell
.\Scripts\build-release.ps1 -BuildInstaller
```

两个架构可以并行发布。x64 使用 `.venv`，ARM64 使用 `.venv-arm64`；安装器分别安装到 `PDFReader-x64` 和 `PDFReader-arm64`，不会互相覆盖。项目目标为 `net10.0`，请确保使用 .NET 10 SDK：

```powershell
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64 -DotnetPath C:\path\to\dotnet10\dotnet.exe
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64 -DotnetPath C:\path\to\dotnet10\dotnet.exe -BuildInstaller
.\Scripts\build-release.ps1 -RuntimeIdentifier both -DotnetPath C:\path\to\dotnet10\dotnet.exe -BuildInstaller
```

ARM64 打包选项会使用 `.venv-arm64` 和 `py-libs/win-arm64` 中的原生 wheel，并在发布前检查 OCR 与 Acrobat 富媒体依赖。当前 ARM64 wheel 清单包括 `numpy`、`opencv-python-headless`、`pyclipper`、`onnxruntime-directml`、`PyMuPDF`、`miniaudio`、`cffi` 和已自行编译的 `pikepdf`。pikepdf wheel 自带 qpdf 12.2.0、zlib 和 MSVC 运行库。

最终安装包名称会使用当前版本号，例如 `PDFReader-1.1.7-x64-Setup.exe` 和 `PDFReader-1.1.7-arm64-Setup.exe`。安装包卸载时会询问是否保留 `user_data`；页面缩略图缓存会被单独删除。

HTML5 阅读包为目录结构，包含 `index.html`、`app.js`、`app.css`、`manifest.json`、`manifest.js`、按页懒加载的 `pages/*.webp` 和关联 `audio/`。网页支持 25%-300% 百分比缩放、页面横向/纵向滚动、在阅读区顶部或底部继续滚轮触发上一页/下一页、OCR 标题/正文搜索定位、显示/隐藏当前页 OCR、本页朗读和从当前页起的连续朗读。页面上下均预留 `25vh`，翻页时按相同的视口留白定位，页面内容保持水平居中。OCR 标题显示在区域外，并通过半透明连接线指向选区。音频按需请求，播放器显示加载中、缓冲中、播放中、暂停和失败状态，并提供当前音频的可拖动进度条和时间显示；连续朗读时仅预加载下一页已有音频，停止或结束后释放预加载对象。`manifest.js` 是为直接双击 `index.html` 准备的内嵌清单；因此不依赖本地服务器也可以打开，`manifest.json` 仍保留供调试和二次处理使用。

## 说明

- TTS API 按 OpenAI Compatible `/audio/speech` 端点请求，并使用 MP3 输出。
- PDF 标注由 `annotation_worker.py` 使用 PyMuPDF 读取、创建、更新、删除并增量写回。复杂或非标准第三方标注可以被选择和删除，但未必能被完全还原为可编辑的同类型对象。
- 详细设计、数据库字段、环境变量和验证清单见 [DEVELOPMENT.md](DEVELOPMENT.md)。
