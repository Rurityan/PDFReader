using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class Html5ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task ExportAsync(
        string sourcePath,
        string outputDirectory,
        string title,
        int pageCount,
        IReadOnlyList<Bookmark> bookmarks,
        IReadOnlyList<OcrRecord> records)
    {
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new InvalidOperationException("HTML5 导出目录必须为空，请选择一个新的空目录。");
        }

        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, "audio"));

        var audioByRecord = new Dictionary<Guid, string>();
        foreach (var record in records)
        {
            var sourceAudio = record.TtsAudios
                .OrderByDescending(audio => audio.CreatedAtUtc)
                .Select(audio => audio.FilePath)
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(sourceAudio))
            {
                continue;
            }

            var extension = Path.GetExtension(sourceAudio);
            var relativePath = $"audio/{record.Id:N}{extension}";
            File.Copy(sourceAudio, Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)), true);
            audioByRecord[record.Id] = relativePath;
        }

        var manifest = new
        {
            format = "PDFReader HTML5 v1",
            title,
            pageCount,
            pageScale = 1.5,
            pages = new { pattern = "pages/page-{0:0000}.webp" },
            bookmarks = bookmarks
                .OrderBy(bookmark => bookmark.SortOrder)
                .ThenBy(bookmark => bookmark.CreatedAtUtc)
                .Select(bookmark => new { bookmark.Id, bookmark.ParentId, bookmark.PageNumber, bookmark.Title, bookmark.SortOrder }),
            ocrRecords = records
                .OrderBy(record => record.PageNumber)
                .ThenBy(record => record.CreatedAtUtc)
                .Select(record => new
                {
                    record.Id, record.BookmarkId, record.PageNumber, record.X, record.Y, record.Width, record.Height,
                    record.CaptureZoom, record.Title, record.Text,
                    audioPath = audioByRecord.TryGetValue(record.Id, out var audioPath) ? audioPath : null,
                }),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), manifestJson);
        // A script copy allows the package to work when index.html is opened
        // directly via file://, where browsers commonly block fetch().
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "manifest.js"),
            $"window.PDFREADER_MANIFEST={manifestJson};");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "index.html"), HtmlTemplate);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "app.css"), CssTemplate);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "app.js"), JavaScriptTemplate);
        await RenderPagesAsync(sourcePath, outputDirectory, pageCount);
    }

    private static async Task RenderPagesAsync(string sourcePath, string outputDirectory, int pageCount)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var python = ResolvePythonPath(baseDirectory);
        var worker = Path.Combine(baseDirectory, "Scripts", "html_export_worker.py");
        if (!File.Exists(python) || !File.Exists(worker))
        {
            throw new FileNotFoundException("找不到 HTML5 导出运行环境。");
        }

        const int batchSize = 16;
        for (var startPage = 1; startPage <= pageCount; startPage += batchSize)
        {
            var endPage = Math.Min(pageCount, startPage + batchSize - 1);
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = baseDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(worker);
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add(startPage.ToString());
            startInfo.ArgumentList.Add(endPage.ToString());
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 HTML5 第 {startPage}-{endPage} 页导出服务。");
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"HTML5 第 {startPage}-{endPage} 页导出失败，Python 进程退出码：{process.ExitCode}。"
                        : $"HTML5 第 {startPage}-{endPage} 页导出失败：{error.Trim()}");
            }
        }
    }

    private static string ResolvePythonPath(string baseDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("PDFREADER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(baseDirectory, ".venv", "Scripts", "python.exe");
    }

    private const string HtmlTemplate = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PDFReader HTML5</title><link rel="stylesheet" href="app.css"></head><body><aside><h1 id="title"></h1><div id="bookmarks"></div></aside><main><header><button id="previous" title="上一页">上一页</button><label>第 <input id="page" type="number" min="1"> / <span id="page-count"></span> 页</label><button id="next" title="下一页">下一页</button><button id="zoom-out">-</button><input id="zoom" value="100%" title="页面缩放比例"><button id="zoom-in">+</button><button id="toggle-ocr">显示 OCR</button><button id="read-page">朗读本页</button><button id="read-all">连续朗读</button><button id="pause" hidden>暂停播放</button><button id="stop" hidden>停止播放</button></header><section id="viewer"><div id="page-wrap"><img id="page-image" alt="PDF 页面"><div id="ocr-layer"></div></div></section></main><audio id="audio"></audio><script src="manifest.js"></script><script src="app.js"></script></body></html>
""";

    private const string CssTemplate = """
*{box-sizing:border-box}body{margin:0;font:14px Arial,sans-serif;color:#27313b;background:#eef1f4;display:grid;grid-template-columns:260px 1fr;height:100vh;overflow:hidden}aside{background:#20252b;color:#fff;padding:18px;overflow:auto}h1{font-size:17px;margin:0 0 18px}#bookmarks button{background:transparent;border:0;color:#dce2e8;cursor:pointer;display:block;text-align:left;padding:6px 0;width:100%;line-height:1.35}#bookmarks button:hover{text-decoration:underline;color:#fff}main{display:grid;grid-template-rows:auto 1fr;min-width:0;min-height:0}header{padding:10px 16px;background:#fff;border-bottom:1px solid #d6dbe0;display:flex;align-items:center;gap:8px;white-space:nowrap;overflow:auto}button,input{font:inherit}button{border:1px solid #c6ced6;background:#fff;border-radius:4px;padding:6px 10px;cursor:pointer}button:hover{background:#f1f3f5}input{width:58px;padding:5px;border:1px solid #c6ced6;border-radius:4px;text-align:center}#zoom{width:68px}#viewer{overflow:auto;padding:0 28px;display:grid;justify-items:center;align-content:start;min-width:0;min-height:0}#page-wrap{position:relative;margin:25vh auto;box-shadow:0 2px 12px #0003;background:#fff;line-height:0;max-width:none;justify-self:center}#page-image{display:block;max-width:none;height:auto}#ocr-layer{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}.ocr{position:absolute;line-height:1.2;color:#142b46;overflow:visible;pointer-events:auto}.ocr-box{position:absolute;inset:0;border:2px solid #2b6cb0;background:#2b6cb024;cursor:pointer}.ocr-label{position:absolute;left:0;bottom:100%;margin-bottom:7px;padding:4px 7px;background:#ffffffd9;border:1px solid #2b6cb080;border-radius:3px;white-space:nowrap;line-height:1.2;max-width:280px;overflow:hidden;text-overflow:ellipsis}.ocr-line{position:absolute;left:12px;bottom:100%;width:1px;height:7px;background:#2b6cb080}.ocr button{position:absolute;right:2px;top:2px;padding:2px 5px;background:#ffffffd9;border:0;font-size:12px;z-index:1}.ocr.hidden{display:none}@media(max-width:720px){body{grid-template-columns:0 1fr}aside{display:none}#viewer{padding:0 12px}#page-wrap{margin:25vh auto}}
.ocr button{left:calc(100% + 6px);right:auto;top:-3px;width:30px;height:30px;padding:0;border:1px solid #2b6cb080;border-radius:50%;background:#ffffffcc;color:#183b5c;font-size:0;line-height:1;box-shadow:0 1px 4px #0002}.ocr button::before{content:'\1F50A';font-size:20px}.ocr button:hover{background:#ffffffe8}
""";

    private const string JavaScriptTemplate = """
const state={manifest:null,page:1,showOcr:false,zoom:1,playing:[],index:0,continuous:false,wheelTravel:0,wheelDirection:0,wheelLocked:false,searchResults:[],searchIndex:-1};const $=id=>document.getElementById(id);const audio=$('audio');const searchStyle=document.createElement('style');searchStyle.textContent='.ocr.current .ocr-box{border-color:#e6a23c;background:#e6a23c35;box-shadow:0 0 0 3px #e6a23c55}.ocr.current .ocr-label{border-color:#e6a23c;background:#fff4d6}';document.head.append(searchStyle);
const loadManifest=window.PDFREADER_MANIFEST?Promise.resolve(window.PDFREADER_MANIFEST):fetch('manifest.json').then(r=>r.json());loadManifest.then(m=>{state.manifest=m;$('title').textContent=m.title;$('page-count').textContent=m.pageCount;$('page').max=m.pageCount;installSearch();buildBookmarks();go(1)}).catch(e=>{document.body.innerHTML='<p style="padding:24px;color:#b42318">无法加载阅读包清单：'+e.message+'</p>'});
function installSearch(){const header=document.querySelector('header'),input=document.createElement('input'),mode=document.createElement('select'),previous=document.createElement('button'),next=document.createElement('button'),count=document.createElement('span');input.id='ocr-search';input.placeholder='搜索 OCR 标题';input.title='按 Enter 定位下一个结果';input.style.cssText='width:180px;text-align:left';mode.id='ocr-search-mode';mode.title='搜索范围';mode.innerHTML='<option value="title">标题</option><option value="text">正文</option>';previous.textContent='↑';previous.title='上一个 OCR 搜索结果';next.textContent='↓';next.title='下一个 OCR 搜索结果';count.id='ocr-search-count';count.style.cssText='min-width:44px;color:#68737e';header.insertBefore(input,$('toggle-ocr'));header.insertBefore(mode,$('toggle-ocr'));header.insertBefore(previous,$('toggle-ocr'));header.insertBefore(next,$('toggle-ocr'));header.insertBefore(count,$('toggle-ocr'));input.oninput=updateSearch;mode.onchange=updateSearch;previous.onclick=previousSearch;next.onclick=nextSearch;input.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();nextSearch()}if(e.key==='Escape'){input.value='';updateSearch()}}}
function pageCount(){return Number($('page-count').textContent)}function path(n){return mpath(state.manifest.pages.pattern,n)}function mpath(p,n){return p.replace('{0:0000}',String(n).padStart(4,'0'))}
function buildBookmarks(){const box=$('bookmarks');state.manifest.bookmarks.forEach(b=>{const x=document.createElement('button');x.textContent=b.title;x.style.paddingLeft=(Math.max(1,b.parentId?2:1)*10)+'px';x.onclick=()=>go(b.pageNumber);box.append(x)})}
function applyLayout(){const page=$('page-wrap');page.style.margin='25vh auto'}function setPagePosition(direction){const viewer=$('viewer'),page=$('page-wrap'),viewerRect=viewer.getBoundingClientRect(),pageRect=page.getBoundingClientRect(),edgeOffset=viewer.clientHeight*.25,pageTop=viewer.scrollTop+(pageRect.top-viewerRect.top),pageBottom=pageTop+pageRect.height,target=direction<0?pageBottom-viewer.clientHeight+edgeOffset:pageTop-edgeOffset,maxScroll=Math.max(0,viewer.scrollHeight-viewer.clientHeight);viewer.scrollTop=Math.max(0,Math.min(maxScroll,target))}
function go(n,preserve=false){if(!state.manifest)return;const previousPage=state.page;const targetPage=Math.min(pageCount(),Math.max(1,Number(n)||1));if(!preserve)stop();state.page=targetPage;$('page').value=state.page;const image=$('page-image');const direction=targetPage<previousPage?-1:1;image.onload=()=>{applyLayout();renderOcr();requestAnimationFrame(()=>setPagePosition(direction))};image.src=path(state.page)}
function normalizeSearch(value){return String(value||'').replace(/[\s]+/g,' ').trim().toLowerCase()}function updateSearch(){const input=$('ocr-search'),mode=$('ocr-search-mode'),count=$('ocr-search-count');if(!input)return;const q=normalizeSearch(input.value);state.searchResults=q?state.manifest.ocrRecords.filter(r=>normalizeSearch(mode.value==='title'?r.title:r.text).includes(q)):[];state.searchIndex=-1;count.textContent=state.searchResults.length?'0 / '+state.searchResults.length:'';renderOcr()}function moveSearch(step){if(!state.searchResults.length)return;state.searchIndex=(state.searchIndex+step+state.searchResults.length)%state.searchResults.length;const record=state.searchResults[state.searchIndex];$('ocr-search-count').textContent=(state.searchIndex+1)+' / '+state.searchResults.length;if(state.page!==record.pageNumber)go(record.pageNumber,true);else{renderOcr();document.querySelector('.ocr.current')?.scrollIntoView({block:'center',inline:'center'})}}function previousSearch(){moveSearch(-1)}function nextSearch(){moveSearch(1)}
function renderOcr(){const layer=$('ocr-layer'),img=$('page-image'),wrap=$('page-wrap');layer.replaceChildren();const scale=state.zoom,base=state.manifest.pageScale||1;const width=img.naturalWidth*scale,height=img.naturalHeight*scale;img.style.width=width+'px';wrap.style.width=width+'px';wrap.style.height=height+'px';const records=state.manifest.ocrRecords.filter(x=>x.pageNumber===state.page);records.forEach(r=>{const x=document.createElement('div');x.className='ocr';x.hidden=!state.showOcr;if(r.id===state.searchResults[state.searchIndex]?.id)x.classList.add('current');const z=r.captureZoom||1;x.style.left=(r.x/z*base*scale)+'px';x.style.top=(r.y/z*base*scale)+'px';x.style.width=(r.width/z*base*scale)+'px';x.style.height=(r.height/z*base*scale)+'px';const label=document.createElement('div');label.className='ocr-label';label.textContent=r.title||r.text;const line=document.createElement('div');line.className='ocr-line';const box=document.createElement('div');box.className='ocr-box';if(r.audioPath){const b=document.createElement('button');b.textContent='播放';b.onclick=e=>{e.stopPropagation();play([r])};box.append(b)}box.append(label,line);x.append(box);layer.append(x)});layer.style.width=width+'px';layer.style.height=height+'px'}
function play(items,continuous=false){state.playing=items.filter(x=>x.audioPath);state.index=0;state.continuous=continuous;nextAudio()}function nextAudio(){if(state.index>=state.playing.length&&state.continuous&&state.page<pageCount()){const next=state.page+1;state.playing.push(...state.manifest.ocrRecords.filter(x=>x.pageNumber===next&&x.audioPath));go(next,true)}const r=state.playing[state.index++];if(!r){stop();return}if(r.pageNumber!==state.page)go(r.pageNumber,true);audio.src=r.audioPath;audio.play();$('pause').hidden=false;$('stop').hidden=false;$('pause').textContent='暂停播放'}function stop(){audio.pause();audio.removeAttribute('src');state.playing=[];state.index=0;state.continuous=false;$('pause').hidden=true;$('stop').hidden=true}
function setZoom(value){state.zoom=Math.min(3,Math.max(.25,Number(value)||1));$('zoom').value=Math.round(state.zoom*100)+'%';renderOcr()}function resetWheel(){state.wheelTravel=0;state.wheelDirection=0}function handleBoundaryWheel(e){const viewer=$('viewer'),atTop=viewer.scrollTop<=1,atBottom=viewer.scrollTop+viewer.clientHeight>=viewer.scrollHeight-1;if(!atTop&&!atBottom){resetWheel();return}const direction=e.deltaY<0?-1:1;if((direction<0&&!atTop)||(direction>0&&!atBottom)){resetWheel();return}if(state.wheelDirection!==direction){state.wheelDirection=direction;state.wheelTravel=0}state.wheelTravel+=Math.abs(e.deltaY);if(state.wheelTravel>=180&&!state.wheelLocked){state.wheelLocked=true;resetWheel();go(state.page+direction);setTimeout(()=>state.wheelLocked=false,260)}}audio.onended=nextAudio;$('previous').onclick=()=>go(state.page-1);$('next').onclick=()=>go(state.page+1);$('page').onchange=e=>go(e.target.value);$('zoom-out').onclick=()=>setZoom(state.zoom-.1);$('zoom-in').onclick=()=>setZoom(state.zoom+.1);$('zoom').onchange=e=>setZoom(String(e.target.value).replace('%','')/100);$('toggle-ocr').onclick=()=>{state.showOcr=!state.showOcr;$('toggle-ocr').textContent=state.showOcr?'隐藏 OCR':'显示 OCR';renderOcr()};$('read-page').onclick=()=>play(state.manifest.ocrRecords.filter(x=>x.pageNumber===state.page),false);$('read-all').onclick=()=>play(state.manifest.ocrRecords.filter(x=>x.pageNumber>=state.page),true);$('pause').onclick=()=>{if(audio.paused){audio.play();$('pause').textContent='暂停播放'}else{audio.pause();$('pause').textContent='继续播放'}};$('stop').onclick=stop;$('viewer').addEventListener('wheel',handleBoundaryWheel,{passive:true});window.onresize=renderOcr;
""";
}
