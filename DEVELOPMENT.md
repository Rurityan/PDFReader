# PDFReader 开发文档

本文档用于同步当前阶段的实现状态、模块职责、数据结构和开发约定。项目仍处于全新开发阶段，暂不承担旧版本数据兼容义务。

## 1. 项目目标

PDFReader 是一款基于 Avalonia 的桌面 PDF 阅读与标注软件，当前核心目标包括：

- 阅读 PDF，并在数据库中管理已打开的 PDF 文档。
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
- PDFsharp 6.2.4：PDF 标注对象的读取、创建、删除和保存
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
├─ MainWindow.axaml            主界面布局和绑定
├─ MainWindow.axaml.cs         页面交互、框选、拖拽和叠加层
├─ template.pdf                当前测试 PDF
└─ user_data/                  运行时数据库、配置和资源
```

## 4. 运行时数据目录

默认路径相对于程序目录，集中在 `user_data` 下：

```text
user_data/
├─ reader.db
├─ settings.json
└─ resource/
   ├─ image/                   OCR 截图缓存目录
   └─ voice/                   TTS 音频目录
```

数据库和配置路径固定为程序目录下的 `user_data`。截图和音频目录可以在设置窗口中修改。截图缓存默认关闭；关闭缓存时，OCR 仍可以在内存中完成识别，但不会额外保留截图文件。

TTS 设置包含以下四项：

- Base URL
- API Key
- Model Type
- Voice Model

API Key 在配置文件中使用 DPAPI 加密保存，设置界面只显示脱敏值。TTS 服务会在 Base URL 后补充 `/audio/speech`（如果用户没有填写该路径）。

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

PDF 标注不写入 SQLite，而是直接保存为 PDF annotation 对象，并使用 `/NM` 保存 UUID 形式的对象标识。

当前数据库由 `EnsureCreated` 初始化。检测到不符合当前开发阶段结构的用户表时，会按全新开发阶段策略重建数据库；后续如果进入稳定发布阶段，再引入正式迁移策略。

## 6. OCR 工作流

1. 用户点击“启动 OCR”后，OCR 功能才进入可用状态。
2. 用户选择“截取一次”或“持续截取”模式。
3. 在页面上框选区域，应用将选区渲染为图片并传给 `Scripts/ocr_worker.py`。
4. OCR 结果先进入待确认状态，不会自动写入数据库。
5. 用户可修改标题和正文，点击“确认并保存 OCR”后才创建数据库记录。
6. OCR 记录可以通过“挂载”按钮挂载到当前选中的书签。
7. 用户可以为 OCR 记录生成 TTS 音频，音频文件写入配置的 voice 目录并建立数据库记录。
8. 应用退出时，尚未挂载到书签的 OCR 历史记录会被直接丢弃；其截图、音频等关联资源也会清理。截图本身不作为受保护数据处理。

OCR 支持取消当前截取，也支持鼠标右键取消。持续截取模式可以连续提交多个区域，当前页 OCR 叠加层可单独显示，并在每个 OCR 框旁提供生成、缓存和播放音频的喇叭按钮。

## 7. 书签工作流

- “创建书签”按钮弹窗设置名称和页码，页码默认使用当前页。
- 书签名称支持右键修改，并在树中实时更新。
- 书签支持拖拽调整层级：拖入目标书签底部区域表示设为子书签，拖到同级落点区域表示设为同级书签。
- 拖拽过程中显示半透明书签和固定落点预览线。
- 右键菜单支持跳转到所在页面、修改名称、脱离父书签和删除。
- 删除含子书签的节点前弹出确认窗口。
- 删除书签支持通过顶部撤回按钮撤回最近一次删除操作。
- 书签树的新增、重命名、移动、脱离和删除变化自动保存。
- 书签树中的 OCR 项带有特殊标记；正文通过右键菜单查看，存在音频时可直接播放，没有音频时可从右键菜单生成。

应用启动时会从数据库恢复 PDF 文档列表。如果数据库中的路径不存在，用户可以重新绑定文件、暂时搁置，或者删除 PDF 对象。删除 PDF 对象时，会同时删除其书签、OCR 记录、音频记录和相关资源。

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

OCR 服务默认使用项目根目录下的：

```text
.venv\Scripts\python.exe
```

如需指定其他 Python 解释器，可设置 `PDFREADER_PYTHON` 环境变量。OCR worker 只需要以下 Python 依赖：

```powershell
python -m pip install -r Scripts/requirements-ocr.txt
```

默认模型目录为 `user_data/resource/ocr/`，其中放置 `det.onnx`、`rec.onnx` 和识别模型的 `inference.yml`（也可使用 `ppocr_keys_v1.txt`）。设备选择通过 `PDFREADER_OCR_DEVICE` 控制：`auto` 默认优先 DirectML，`cpu` 强制 CPU，`directml` 强制 DirectML。也可以用 `PDFREADER_OCR_MODEL_DIR`、`PDFREADER_OCR_DET_MODEL`、`PDFREADER_OCR_REC_MODEL` 和 `PDFREADER_OCR_DICTIONARY` 覆盖资源路径。

建议验证以下流程：

1. 使用根目录 `template.pdf` 打开多页 PDF。
2. 测试一次截取、持续截取、取消截取和右键取消。
3. 确认 OCR 结果后检查 `OcrRecords` 是否产生记录，取消确认时不应产生记录。
4. 创建多级书签，测试拖拽层级、重命名、跳转、删除和撤回。
5. 为已保存 OCR 生成音频，检查播放和 `TtsAudioRecords` 状态。
6. 创建各类标注，确认页面立即显示、未点击保存前 PDF 文件不变，点击保存后可以重新读取。
7. 关闭应用时测试未保存标注和未挂载 OCR 的处理提示。

近期已验证：`dotnet build --no-restore` 无警告、无错误；PDF 标注服务已覆盖线条、高亮、方框、自由绘制的创建、读取、颜色/宽度读取和删除烟测；应用启动烟测正常；`git diff --check` 通过。

## 10. 当前已知限制

- OCR 和 TTS 依赖外部 Python 环境及外部 HTTP 服务，服务不可用时只能显示错误，不能离线替代。
- TTS 当前按 OpenAI Compatible `/audio/speech` 请求格式发送，并固定请求 MP3 输出。
- 截图路径主要用于调试和记录，不作为 OCR 数据的可靠存档。
- 标注缓存只存在于当前应用会话；应用异常退出时无法保证未保存标注可恢复。
- PDF 渲染使用 MuPDFCore，标注文件读写使用 PDFsharp，两者对极端 PDF 页面变换或非标准 annotation 的显示可能存在差异。
- 数据库目前按全新开发阶段初始化，没有针对旧数据库的兼容迁移。

## 11. 后续计划

- 完善标注缓存的操作历史和更细粒度撤销/重做。
- 增加更多 PDF 标注属性，例如透明度、端点样式和文本标注编辑。
- 改善大型 PDF、多页预加载和 OCR/TTS 后台任务的取消与进度反馈。
- 在功能稳定后补充自动化测试、数据库迁移和发布打包流程。
