# PDFReader 开发文档

本文档用于同步当前阶段的实现状态、模块职责、数据结构和开发约定。项目仍处于全新开发阶段，暂不承担旧版本数据兼容义务。

## 1. 项目目标

PDFReader 是一款基于 Avalonia 的桌面 PDF 阅读与标注软件，当前核心目标包括：

- 阅读 PDF，并在数据库中管理已打开的 PDF 文档。
- 提供页码输入跳转、虚拟化连续滚动阅读和低倍率页面缩略图预览，并缓存缩略图以减少重复渲染。
- 框选页面区域，调用 PP-OCR ONNX 模型识别中文等文本。
- 由用户确认 OCR 结果后保存历史记录，并将 OCR 记录挂载到多级书签。
- 调用 OpenAI Compatible TTS API 生成语音文件，并在页面或书签树中播放。
- 创建符合 PDF 通用规范的文本、线条、高亮、方框和自由绘制标注。
- 在界面立即预览标注变化，但由用户明确操作后才写回 PDF 文件。

## 2. 技术栈

- .NET 10
- Avalonia 12.1.1、Fluent Theme、Inter Fonts
- CommunityToolkit.Mvvm 8.4.2
- MuPDFCore 1.10.2：PDF 页面渲染
- PyMuPDF 1.26.7：PDF 标注对象的读取、创建、删除、增量保存，以及全量导出元数据附件
- Entity Framework Core SQLite 10.0.10：应用数据库
- SQLitePCLRaw.lib.e_sqlite3 2.1.12：SQLite 原生运行库
- LibVLCSharp 3.10.1、VideoLAN.LibVLC.Windows 3.0.23.1：音频播放
- PP-OCR ONNX det/rec 模型：由独立 Python worker 进程调用
- ONNX Runtime DirectML：Windows 上优先使用 GPU，自动回退 CPU
- OpenAI Compatible TTS REST API：语音生成
- `Microsoft.Extensions.Hosting`：后台任务和应用服务基础设施
- DPAPI：TTS API Key 加密保存

## 3. 目录和模块

```text
PDFReader/
├─ Data/                       EF Core DbContext
├─ Models/                     数据模型、设置模型、标注模型
├─ Services/
│  ├─ PdfDocumentService.cs    MuPDF 页面渲染
│  ├─ PdfEditingService.cs     PDF 标注读写
│  ├─ OcrService.cs            启动 Python OCR worker
│  ├─ TtsService.cs            调用 TTS API 并保存音频
│  ├─ ReaderDbContextFactory.cs 数据库初始化和上下文创建
│  └─ ...                      设置、书签、OCR 等仓储服务
├─ ViewModels/
│  └─ MainWindowViewModel.cs   主界面状态和主要业务流程
├─ Scripts/
│  └─ ocr_worker.py            ONNX Runtime OCR Python worker
├─ ocr_model/                  OCR ONNX 模型和识别字典
├─ Installer/
│  └─ PDFReader.iss            Inno Setup 安装脚本
├─ MainWindow.axaml            主界面布局和绑定
├─ MainWindow.axaml.cs         页面交互、框选、拖拽和叠加层
├─ template.pdf                当前测试 PDF
└─ user_data/                  运行时数据库、配置和资源
```

## 4. 运行时数据目录

模型与运行时数据相对于程序目录分别存放：

```text
ocr_model/
├─ det.onnx                    OCR 检测模型
├─ rec.onnx                    OCR 识别模型
└─ inference.yml               OCR 识别字典

user_data/
├─ reader.db
├─ settings.json
├─ cache/                      页面缩略图缓存
│  └─ {PdfDocumentId}/
│     ├─ metadata.json         PDF 文件签名和缩略图参数
│     └─ page-0001.png ...     低倍率页面缩略图
└─ resource/
   ├─ image/                   OCR 截图缓存目录
   └─ voice/                   TTS 音频目录
```

数据库和配置路径固定为程序目录下的 `user_data`。截图和音频目录可以在设置窗口中修改。页面缩略图缓存固定使用 `user_data/cache`，按 PDF 文档 UUID 分目录保存；PDF 文件大小、最后修改时间、页数或缩放参数变化时会自动失效并重建。删除或重新绑定 PDF 时会清理对应缩略图缓存。缩略图列表使用虚拟化容器，运行时只为可见项按需解码 Bitmap，离开可视区后释放图像内存。

主阅读区同样使用虚拟化多页列表，而不是在滚动事件中重定位单页画布。每个页面先创建固定尺寸的占位容器；进入视口后异步读取低倍率缓存预览，滚动停止约 220ms 后仅为仍可见的页面提交高清渲染。高清渲染在后台限流队列中执行，离开视口的页面会释放预览与高清 Bitmap，已经失效的任务结果直接丢弃。连续阅读时，顶部当前页以视口中心所在页为准；翻页和页码跳转仅滚动虚拟列表，不触发隐藏单页渲染。进入 OCR 截取或标注模式前，才按需将当前页加载到单页交互画布。

连续阅读的每个虚拟页可承载只读 OCR 覆盖层。点击“显示当前页 OCR”会在视口当前页绘制已保存 OCR 的选区边框和喇叭按钮；该层不承担框选、OCR 编辑或标注编辑。喇叭按钮默认只播放已有音频；设置中的“自动生成缺失音频”启用后，才会在无音频时生成并播放。

OCR 选区截图缓存默认关闭；关闭缓存时，OCR 仍可以在内存中完成识别，但不会额外保留截图文件。缩略图缓存与 OCR 选区截图缓存是两套独立机制。

TTS 设置包含以下四项：

- Base URL
- API Key
- Model Type
- Voice Model：从名称下拉菜单选择

Voice Model 维护一个可增删的键值对列表。配置中的数组元素格式如下，`name` 用于界面选择，`voice_id` 用于实际 TTS 请求：

```json
[
  { "name": "xx", "voice_id": "yyy" }
]
```

书签树 OCR 叶子项的右键菜单支持查看正文、播放音频、以默认或指定 Voice Model 生成音频、以指定模型重新生成音频，以及移除记录。重新生成仅在已有音频时可用：新音频成功生成后，才删除旧音频数据库记录和资源文件。

当前选中的名称保存在 `TtsVoiceModel`，列表保存在 `TtsVoiceModels`。TTS 请求会查找对应的 `voice_id` 后发送。

API Key 在配置文件中使用 DPAPI 加密保存，设置界面只显示脱敏值。TTS 服务会在 Base URL 后补充 `/audio/speech`（如果用户没有填写该路径）。

### 本地自动化导入

应用内置本机 REST 服务，默认关闭；用户在设置页启用后监听 `http://127.0.0.1:{port}`，端口也在设置页配置，默认 `38421`。`POST /api/v1/import/ocr-tts` 要求请求头 `X-PDFReader-Token` 与 `settings.json` 中的 `LocalApiToken` 一致。服务接收 `pdfPath` 与 OCR 记录数组；每条记录包含页码、PDF 页面坐标区域、OCR 文本、可选标题、可选本地音频文件路径。服务通过仓储写入数据，复制音频到资源目录，并按页自动关联最深层书签。接口不接收远程 URL，不执行 OCR 区域判断或 TTS 推理，外部自动化应先完成这些步骤。

## 4.1 PDF 全量导出与恢复

“文件 > 全量导出 PDF”生成新的 PDF 副本，不修改当前打开的文件。导出内容包括：

- 已保存的 PDF 标注；
- 标准 PDF Outline，供其他阅读器显示书签目录；
- 作为 PDF Embedded File 的 TTS 音频；
- `PDFReader-metadata.json` 附件，保存书签 UUID/父级关系、OCR 文本与选区、音频关联。

导入文件时，若当前 PDF 记录没有本地书签，应用先尝试读取 `PDFReader-metadata.json` 并恢复书签树、OCR 与音频文件；未检测到该附件时，回退到读取普通 PDF Outline。恢复副本会分配新的本地 UUID，避免与原 PDF 记录的数据库主键冲突。

应用启动后会在后台运行一次一致性清理：删除无效文档或书签关联的 OCR、无主音频记录及其已知资源；失效的书签父级关系会被提升为根书签。REST 导入的未挂载 OCR 会标记为外部导入并保留，直到挂载到书签；手动产生的未挂载 OCR 仍会按退出清理规则处理。该任务不扫描资源目录删除未被数据库引用的任意文件。

## 5. 数据库结构

数据库使用 SQLite，由 `ReaderDbContext` 管理。主键均为 UUID。

### `PdfDocuments`

| 字段 | 说明 |
| --- | --- |
| `Id` | PDF 文档 UUID |
| `FilePath` | PDF 文件路径，唯一索引 |
| `Title` | 文档标题 |
| `CreatedAtUtc` | 创建时间 |
| `LastOpenedAtUtc` | 最近打开时间 |
| `IsArchived` | 是否归档 |

### `Bookmarks`

| 字段 | 说明 |
| --- | --- |
| `Id` | 书签 UUID |
| `PdfDocumentId` | 所属 PDF |
| `ParentId` | 父书签，可为空 |
| `PageNumber` | 所在页码 |
| `Title` | 书签名称 |
| `SortOrder` | 同级排序值 |
| `CreatedAtUtc` | 创建时间 |
| `UpdatedAtUtc` | 更新时间 |

书签支持多级目录。删除 PDF 会级联删除其书签；删除父书签会级联删除子书签。OCR 记录挂载到书签时使用书签 UUID 关联。

### `OcrRecords`

| 字段 | 说明 |
| --- | --- |
| `Id` | OCR 记录 UUID |
| `PdfDocumentId` | 所属 PDF |
| `BookmarkId` | 所属书签，可为空 |
| `PageNumber` | OCR 所在页码 |
| `X`, `Y`, `Width`, `Height` | 截取区域坐标和尺寸 |
| `CaptureZoom` | 截取时页面缩放值 |
| `Title` | OCR 标题，默认取正文前若干字符 |
| `Text` | OCR 正文 |
| `CapturePath` | 截图路径，仅作记录用途 |
| `CreatedAtUtc` | 创建时间 |

删除 PDF 会级联删除 OCR 记录；删除书签时，关联 OCR 的 `BookmarkId` 会被置空。OCR 记录挂载到书签后即自动保存相关书签变化。

### `TtsAudioRecords`

| 字段 | 说明 |
| --- | --- |
| `Id` | 音频记录 UUID |
| `OcrRecordId` | 关联 OCR 记录 |
| `FilePath` | 音频文件路径 |
| `CreatedAtUtc` | 生成时间 |

音频记录随 OCR 记录级联删除。播放前会检查文件是否存在，OCR 项会显示当前是否有可用音频。

顶部“朗读”按钮从当前页开始，按页向后连续播放所有可用 VOC 音频，并在进入下一页时同步翻页；播放中按钮显示“停止播放”。旁边的“朗读本页”按钮只播放当前页已有的 VOC，不自动生成音频、不翻页。音频自然结束、出错或手动停止后恢复正常状态。

PDF 标注不写入 SQLite，而是直接保存为 PDF annotation 对象，并使用 `/NM` 保存 UUID 形式的对象标识。

当前数据库由 `EnsureCreated` 初始化。检测到不符合当前开发阶段结构的用户表时，会按全新开发阶段策略重建数据库；后续如果进入稳定发布阶段，再引入正式迁移策略。

## 6. OCR 工作流

1. 用户点击“启动 OCR”后，OCR 功能才进入可用状态。
2. 用户选择“截取一次”或“持续截取”模式。
3. 在页面上框选区域，应用将选区渲染为图片并传给 `Scripts/ocr_worker.py`。
4. OCR 结果先进入待确认状态，不会自动写入数据库。
5. 用户可修改标题和正文，点击“确认并保存 OCR”后才创建数据库记录。
6. OCR 记录可以通过“挂载”按钮挂载到当前选中的书签，也可以在书签树中拖拽到另一个书签下重新挂载。
7. 用户可以为 OCR 记录生成 TTS 音频，音频文件写入配置的 voice 目录并建立数据库记录。
8. 保存 OCR 时，若当前页存在书签则自动挂载：优先当前选中的同页书签，否则选择该页最深层书签。没有对应书签时，OCR 保持未挂载状态。
9. 应用退出时，尚未挂载到书签的 OCR 历史记录会被直接丢弃；其截图、音频等关联资源也会清理。截图本身不作为受保护数据处理。

OCR 支持取消当前截取，也支持鼠标右键取消。持续截取模式可以连续提交多个区域，当前页 OCR 叠加层可单独显示，并在每个 OCR 框旁提供生成、缓存和播放音频的喇叭按钮。

## 7. 书签工作流

- “创建书签”按钮弹窗设置名称和页码，页码默认使用当前页。
- 创建书签弹窗支持按 Enter 键确认。
- 书签名称支持右键修改，并在树中实时更新。
- 书签名称具有悬停下划线和手形光标，单击名称会跳转到所属页面。
- 书签支持拖拽调整层级：拖入目标书签底部区域表示设为子书签，拖到同级落点区域表示设为同级书签。
- 拖拽过程中显示半透明书签和固定落点预览线。
- 右键菜单支持跳转到所在页面、修改名称、脱离父书签和删除。
- 删除含子书签的节点前弹出确认窗口。
- 删除书签支持通过顶部撤回按钮撤回最近一次删除操作。
- 书签树的新增、重命名、移动、脱离和删除变化自动保存。
- 书签树中的 OCR 项带有特殊标记：没有音频时显示 `OCR`，存在可用音频时显示绿色 `VOC`。OCR 为叶子节点，显示在所属书签的子书签之前；正文、播放、指定模型生成/重新生成和移除记录均通过右键菜单完成。
- 书签树中的 OCR 项不显示常驻喇叭按钮，但仍支持拖拽到其他书签重新挂载。

左侧最外层控制条用于在“页面缩略图”和“书签树”之间切换；两者共享同一块可伸缩区域。顶部工具栏提供当前页指示、页码输入和“跳转”操作。

左侧的“PDF 工作区”只显示当前会话已经导入的 PDF。点击顶部“打开 PDF”会打开 PDF 记录选择窗口：窗口展示数据库中的全部 PDF 记录，支持多选并导入工作区；点击“新 PDF”可以通过系统文件选择器添加新的 PDF 记录。新文件路径会与已有记录按绝对路径去重，重复路径不会创建新的记录。

应用启动时会从数据库恢复可供选择的 PDF 文件记录；只有导入工作区后才会出现在左侧切换列表中。如果数据库中的路径不存在，用户可以重新绑定文件、暂时搁置，或者删除 PDF 对象。删除 PDF 对象时，会同时删除其书签、OCR 记录、音频记录和相关资源。

## 8. PDF 标注

当前支持以下 PDF 通用标注类型：

- 文本：`/Text`
- 线条：`/Line`
- 高亮：`/Highlight`
- 方框：`/Square`
- 自由绘制：`/Ink`

线条、方框和自由绘制支持连续标注模式。当前工具选择后默认进入自由绘制；工具栏会显示当前类型、颜色、线宽和线宽预览。颜色支持常见预设以及 RGB/HEX 自定义拾色窗口。不同工具会使用对应的鼠标指针提示，例如铅笔、荧光笔和橡皮擦。

橡皮擦按照鼠标轨迹命中并删除标注，而不是要求精确点击标注对象。

标注采用两层状态：

- 显示层：创建、删除或修改后立即在页面叠加层中显示。
- 文件层：变更先保存在当前会话的内存缓存中，不会立即修改 PDF。

用户点击“保存标注”后，缓存中的操作才通过 `PdfEditingService` 写回 PDF。应用关闭时如果存在未保存标注，会弹窗让用户选择保存、放弃或取消关闭。顶部“文件”菜单提供增量保存 PDF 和全量“另存为”操作。

## 9. 开发与验证

在项目根目录执行：

```powershell
dotnet restore
dotnet build
dotnet run
```

只验证已恢复依赖的构建时可以使用：

```powershell
dotnet build --no-restore
```

Release 发布 Windows x64 版本：

```powershell
dotnet restore
dotnet publish .\PDFReader.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o .\publish\win-x64
```

发布目录已经包含 `PDFReader.exe`、`Scripts/ocr_worker.py`、OCR 依赖清单和 `ocr_model` 模型。`.NET` 使用 self-contained 发布，但 Python 仍需单独准备。可在发布目录创建虚拟环境：

```powershell
Set-Location .\publish\win-x64
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\Scripts\requirements-ocr.txt
.\PDFReader.exe
```

也可以不在发布目录创建 `.venv`，通过 `PDFREADER_PYTHON` 环境变量指向已有 Python 解释器。应用应从发布目录启动，以便数据库、配置和资源都位于 exe 旁边的 `user_data` 中。

也可以使用 `Scripts/build-release.ps1` 将当前 Python 虚拟环境复制进发布目录；安装 Inno Setup 后，加上 `-BuildInstaller` 参数即可生成安装包：

```powershell
.\Scripts\build-release.ps1 -BuildInstaller
```

ARM64 Windows 发布使用独立输出目录和 ARM64 Python 虚拟环境：

```powershell
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64
.\Scripts\build-release.ps1 -RuntimeIdentifier win-arm64 -BuildInstaller
```

安装包会将 Python、ONNX Runtime、OCR worker 和 `ocr_model` 一起安装。卸载时会询问是否删除 `user_data`；选择保留即可保留数据库、设置、截图和语音资源。

OCR 服务默认使用项目根目录下的：

```text
.venv\Scripts\python.exe
```

如需指定其他 Python 解释器，可设置 `PDFREADER_PYTHON` 环境变量。OCR worker 只需要以下 Python 依赖：

```powershell
python -m pip install -r Scripts/requirements-ocr.txt
```

默认模型目录为应用目录下的 `ocr_model/`，内置官方 PP-OCRv5 server ONNX 模型：`det.onnx`、`rec.onnx` 和识别模型的 `inference.yml`（也可使用 `ppocr_keys_v1.txt`）。该组合面向高准确率中文、繁体中文、英文和日文识别；检测长边默认按 960 像素预处理，识别输入固定高 48 像素。设备选择通过 `PDFREADER_OCR_DEVICE` 控制：`auto` 默认优先 DirectML，`cpu` 强制 CPU，`directml` 强制 DirectML。也可以用 `PDFREADER_OCR_MODEL_DIR`、`PDFREADER_OCR_DET_MODEL`、`PDFREADER_OCR_REC_MODEL` 和 `PDFREADER_OCR_DICTIONARY` 覆盖资源路径。

Adobe Acrobat 富媒体导出通过 `Scripts/rich_media_worker.py`、`pikepdf` 和 `miniaudio` 写入 PDF 标准 Sound Annotation；普通启动、OCR、标注保存和 PDFReader 全量导出不加载这些库。x64 的 `requirements-ocr.txt` 和安装包已包含这些依赖，发布脚本会校验 `pikepdf` 可被导入。MP3 会在导出时解码为 44100 Hz、单声道、16-bit Signed PCM，以避免 Acrobat 将 MP3 字节误当采样数据而播放噪声。同一 OCR 区域有多个音频时，按钮会横向错开；按钮位于 OCR 区域右上角，尺寸约 14-20 pt 并带半透明显示；PDF 坐标会从页面左上原点转换为 PDF 左下原点。导出的音频会作为 PDF 附件保存，并在对应 OCR 区域生成可由 Acrobat 识别的声音注释；PDFReader 专用 OCR 清单也会一并保留。`pikepdf 9.10.2` 暂无 Windows ARM64 官方 wheel，因此 ARM64 发布包不包含该依赖，调用导出时会显示安装提示。

建议验证以下流程：

1. 使用根目录 `template.pdf` 打开多页 PDF，测试页码输入跳转和缩略图点击跳页。
2. 在首次打开和再次打开同一 PDF 时检查 `user_data/cache/{PdfDocumentId}` 的缩略图缓存命中；修改 PDF 后确认缓存重建。
3. 测试左侧控制条在页面缩略图和书签树之间切换，并确认面板占满剩余高度。
4. 使用大 PDF 快速拖动主阅读区滚动条，确认先出现占位/预览、停止滚动后再回填高清页，且当前页指示随视口中心更新。
5. 测试一次截取、持续截取、取消截取和右键取消。
6. 确认 OCR 结果后检查 `OcrRecords` 是否产生记录，取消确认时不应产生记录。
7. 创建多级书签，测试拖拽层级、OCR 重新挂载、重命名、名称点击跳转、删除和撤回。
8. 配置多个 Voice Model，测试名称下拉选择、键值对增删，以及实际请求使用 `voice_id`。
9. 为已保存 OCR 生成音频，检查 `TtsAudioRecords`、`OCR/VOC` 标记和顶部“朗读/停止播放”状态。
10. 创建各类标注，确认页面立即显示、未点击保存前 PDF 文件不变，点击保存后可以重新读取。
11. 关闭应用时测试未保存标注和未挂载 OCR 的处理提示。

近期已验证：Release `dotnet build --no-restore` 无警告、无错误；PDF 标注服务已覆盖线条、高亮、方框、自由绘制的创建、读取、颜色/宽度读取和删除烟测；应用启动烟测正常；Inno Setup Release 安装包可生成；`git diff --check` 通过。

## 10. 当前已知限制

- OCR 和 TTS 依赖外部 Python 环境及外部 HTTP 服务，服务不可用时只能显示错误，不能离线替代。
- TTS 当前按 OpenAI Compatible `/audio/speech` 请求格式发送，并固定请求 MP3 输出。
- Voice Model 列表和当前选择保存在 `settings.json`；如果当前选择没有对应的 `voice_id`，TTS 请求会被拒绝并提示重新配置。
- 截图路径主要用于调试和记录，不作为 OCR 数据的可靠存档。
- 标注缓存只存在于当前应用会话；应用异常退出时无法保证未保存标注可恢复。
- PDF 渲染使用 MuPDFCore，标注文件读写使用 PDFsharp，两者对极端 PDF 页面变换或非标准 annotation 的显示可能存在差异。
- 数据库目前按全新开发阶段初始化，没有针对旧数据库的兼容迁移。
- 页面缩略图属于非核心缓存，卸载时无条件删除 `user_data/cache`；即使选择保留其他 `user_data`，也不会保留缩略图缓存。

## 11. 后续计划

- 完善标注缓存的操作历史和更细粒度撤销/重做。
- 增加更多 PDF 标注属性，例如透明度、端点样式和文本标注编辑。
- 改善大型 PDF、多页缩略图预加载和 OCR/TTS 后台任务的取消与进度反馈。
- 在功能稳定后补充自动化测试、数据库迁移和发布打包流程。
