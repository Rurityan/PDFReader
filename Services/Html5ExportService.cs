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
                .SelectMany(EnumerateBookmarkTree)
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
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "app.js"), JavaScriptTemplate + AudioControlsScript);
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

    private static IEnumerable<Bookmark> EnumerateBookmarkTree(Bookmark bookmark)
    {
        yield return bookmark;
        foreach (var child in bookmark.Children
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.CreatedAtUtc))
        {
            foreach (var descendant in EnumerateBookmarkTree(child))
            {
                yield return descendant;
            }
        }
    }

    private const string HtmlTemplate = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PDFReader HTML5</title><link rel="stylesheet" href="app.css"></head><body><aside><h1 id="title"></h1><div id="bookmarks"></div></aside><main><header><button id="previous" title="上一页">上一页</button><label>第 <input id="page" type="number" min="1"> / <span id="page-count"></span> 页</label><button id="next" title="下一页">下一页</button><button id="zoom-out">-</button><input id="zoom" value="100%" title="页面缩放比例"><button id="zoom-in">+</button><button id="toggle-ocr">显示 OCR</button><button id="read-page">朗读本页</button><button id="read-all">连续朗读</button><button id="pause" hidden>暂停播放</button><button id="stop" hidden>停止播放</button><div id="audio-panel" aria-live="polite"><span id="audio-status">未播放</span><input id="audio-progress" type="range" min="0" max="100" value="0" step="0.1" aria-label="音频播放进度"><span id="audio-time">00:00 / 00:00</span></div></header><section id="viewer"><div id="page-wrap"><img id="page-image" alt="PDF 页面"><div id="ocr-layer"></div></div></section></main><audio id="audio" preload="none"></audio><script src="manifest.js"></script><script src="app.js"></script></body></html>
""";

    private const string CssTemplate = """
*{box-sizing:border-box}body{margin:0;font:14px Arial,sans-serif;color:#27313b;background:#eef1f4;display:grid;grid-template-columns:260px 1fr;height:100vh;overflow:hidden}aside{background:#20252b;color:#fff;padding:18px;overflow:auto}h1{font-size:17px;margin:0 0 18px}#bookmarks{font-size:14px}#bookmarks details{margin:0;padding:0}#bookmarks summary{padding:6px 0;color:#dce2e8;cursor:pointer;list-style-position:inside;line-height:1.35}#bookmarks summary:hover{text-decoration:underline;color:#fff}#bookmarks .bookmark-leaf{display:block;width:100%;padding:6px 0 6px 18px;background:transparent;border:0;border-radius:0;color:#dce2e8;cursor:pointer;text-align:left;line-height:1.35}#bookmarks .bookmark-leaf:hover{text-decoration:underline;color:#fff}#bookmarks .bookmark-children{padding-left:14px}main{display:grid;grid-template-rows:auto 1fr;min-width:0;min-height:0}header{padding:10px 16px;background:#fff;border-bottom:1px solid #d6dbe0;display:flex;align-items:center;gap:8px;white-space:nowrap;overflow:auto}button,input{font:inherit}button{border:1px solid #c6ced6;background:#fff;border-radius:4px;padding:6px 10px;cursor:pointer}button:hover{background:#f1f3f5}input{width:58px;padding:5px;border:1px solid #c6ced6;border-radius:4px;text-align:center}#zoom{width:68px}#audio-panel{display:flex;align-items:center;gap:6px;min-width:250px;max-width:360px;padding-left:4px;color:#68737e;font-size:12px}#audio-status{min-width:56px}#audio-progress{width:120px;padding:0;accent-color:#d39b22}#audio-time{min-width:78px;font-variant-numeric:tabular-nums}#viewer{overflow:auto;padding:0 28px;display:grid;justify-items:center;align-content:start;min-width:0;min-height:0}#page-wrap{position:relative;margin:25vh auto;box-shadow:0 2px 12px #0003;background:#fff;line-height:0;max-width:none;justify-self:center}#page-image{display:block;max-width:none;height:auto}#ocr-layer{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}.ocr{position:absolute;line-height:1.2;color:#142b46;overflow:visible;pointer-events:auto}.ocr-box{position:absolute;inset:0;border:2px solid #2b6cb0;background:#2b6cb024;cursor:pointer}.ocr-label{position:absolute;left:0;bottom:100%;margin-bottom:7px;padding:4px 7px;background:#ffffffd9;border:1px solid #2b6cb080;border-radius:3px;white-space:nowrap;line-height:1.2;max-width:280px;overflow:hidden;text-overflow:ellipsis}.ocr-line{position:absolute;left:12px;bottom:100%;width:1px;height:7px;background:#2b6cb080}.ocr button{position:absolute;right:2px;top:2px;padding:2px 5px;background:#ffffffd9;border:0;font-size:12px;z-index:1}.ocr.hidden{display:none}@media(max-width:720px){body{grid-template-columns:0 1fr}aside{display:none}#viewer{padding:0 12px}#audio-panel{min-width:220px}#audio-progress{width:90px}#page-wrap{margin:25vh auto}}
.ocr button{left:calc(100% + 6px);right:auto;top:-3px;width:30px;height:30px;padding:0;border:1px solid #2b6cb080;border-radius:50%;background:#ffffffcc;color:#183b5c;font-size:0;line-height:1;box-shadow:0 1px 4px #0002}.ocr button::before{content:'\1F50A';font-size:20px}.ocr button:hover{background:#ffffffe8}
@media(max-width:720px){body{display:grid;grid-template-columns:1fr;grid-template-rows:auto 1fr;height:100dvh}aside{display:block;max-height:28dvh;padding:10px 14px;border-bottom:1px solid #39434d}h1{font-size:15px;margin:0 0 7px}#bookmarks{max-height:19dvh;overflow:auto}main{min-height:0}header{padding:8px 10px;gap:6px;overflow-x:auto;overflow-y:hidden;-webkit-overflow-scrolling:touch}header button{flex:0 0 auto}#viewer{padding:0 8px;touch-action:pan-x pan-y;-webkit-overflow-scrolling:touch}#page-wrap{margin:25vh auto}#audio-panel{min-width:230px;max-width:none}#audio-progress{width:90px}}
@media(max-width:420px){aside{max-height:24dvh}#bookmarks{max-height:15dvh}#audio-panel{min-width:210px}#audio-time{min-width:72px;font-size:11px}}
@media(min-width:721px){#toggle-bookmarks{display:none}}
@media(max-width:720px){body:not(.bookmarks-open) aside{transform:translateX(-105%)}aside{position:fixed;left:0;top:0;bottom:0;width:min(82vw,320px);max-height:none;z-index:20;padding:14px;box-shadow:4px 0 18px #0006;transition:transform .18s ease}body.bookmarks-open aside{transform:translateX(0)}#bookmarks{max-height:none;height:calc(100% - 34px)}#toggle-bookmarks{display:block;flex:0 0 auto}main{grid-row:1 / -1}}
@media(max-width:720px){aside{width:min(68vw,280px);overflow:auto}#bookmarks{width:max-content;min-width:100%;overflow:visible}}
""";

    private const string JavaScriptTemplate = """
const state={manifest:null,page:1,showOcr:false,zoom:1,playing:[],index:0,continuous:false,wheelTravel:0,wheelDirection:0,wheelLocked:false,searchResults:[],searchIndex:-1,storedView:{}};const $=id=>document.getElementById(id);const audio=$('audio');const searchStyle=document.createElement('style');searchStyle.textContent='.ocr.current .ocr-box{border-color:#e6a23c;background:#e6a23c35;box-shadow:0 0 0 3px #e6a23c55}.ocr.current .ocr-label{border-color:#e6a23c;background:#fff4d6}';document.head.append(searchStyle);
function viewStorageKey(){return 'pdfreader-html5-view-'+encodeURIComponent((state.manifest?.title||'document')+'-'+(state.manifest?.pageCount||0))}function readViewState(){try{return JSON.parse(localStorage.getItem(viewStorageKey())||'{}')}catch{return {}}}function writeViewState(){try{localStorage.setItem(viewStorageKey(),JSON.stringify(state.storedView))}catch{}}
const loadManifest=window.PDFREADER_MANIFEST?Promise.resolve(window.PDFREADER_MANIFEST):fetch('manifest.json').then(r=>r.json());loadManifest.then(m=>{state.manifest=m;state.storedView=readViewState();$('title').textContent=m.title;$('page-count').textContent=m.pageCount;$('page').max=m.pageCount;installSearch();buildBookmarks();go(state.storedView.page||1)}).catch(e=>{document.body.innerHTML='<p style="padding:24px;color:#b42318">无法加载阅读包清单：'+e.message+'</p>'});
function installSearch(){const header=document.querySelector('header'),input=document.createElement('input'),mode=document.createElement('select'),previous=document.createElement('button'),next=document.createElement('button'),count=document.createElement('span');input.id='ocr-search';input.placeholder='搜索 OCR 标题';input.title='按 Enter 定位下一个结果';input.style.cssText='width:180px;text-align:left';mode.id='ocr-search-mode';mode.title='搜索范围';mode.innerHTML='<option value="title">标题</option><option value="text">正文</option>';previous.textContent='↑';previous.title='上一个 OCR 搜索结果';next.textContent='↓';next.title='下一个 OCR 搜索结果';count.id='ocr-search-count';count.style.cssText='min-width:44px;color:#68737e';header.insertBefore(input,$('toggle-ocr'));header.insertBefore(mode,$('toggle-ocr'));header.insertBefore(previous,$('toggle-ocr'));header.insertBefore(next,$('toggle-ocr'));header.insertBefore(count,$('toggle-ocr'));input.oninput=updateSearch;mode.onchange=updateSearch;previous.onclick=previousSearch;next.onclick=nextSearch;input.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();nextSearch()}if(e.key==='Escape'){input.value='';updateSearch()}}}
function pageCount(){return Number($('page-count').textContent)}function path(n){return mpath(state.manifest.pages.pattern,n)}function mpath(p,n){return p.replace('{0:0000}',String(n).padStart(4,'0'))}
function buildBookmarks(){const box=$('bookmarks');box.replaceChildren();const entries=state.manifest.bookmarks||[],children=new Map();entries.forEach(bookmark=>{const key=bookmark.parentId||null;if(!children.has(key))children.set(key,[]);children.get(key).push(bookmark)});for(const list of children.values())list.sort((a,b)=>(a.sortOrder??0)-(b.sortOrder??0));function append(parent,container){(children.get(parent)||[]).forEach(bookmark=>{const nested=children.get(bookmark.id)||[];if(nested.length){const details=document.createElement('details');details.open=state.storedView.expanded?.[bookmark.id]===true;details.addEventListener('toggle',()=>{state.storedView.expanded=state.storedView.expanded||{};state.storedView.expanded[bookmark.id]=details.open;writeViewState()});const summary=document.createElement('summary');summary.textContent=bookmark.title;summary.title='跳转到第 '+bookmark.pageNumber+' 页';summary.onclick=e=>{if(e.target===summary)go(bookmark.pageNumber)};details.append(summary);const childBox=document.createElement('div');childBox.className='bookmark-children';append(bookmark.id,childBox);details.append(childBox);container.append(details)}else{const leaf=document.createElement('button');leaf.className='bookmark-leaf';leaf.textContent=bookmark.title;leaf.title='跳转到第 '+bookmark.pageNumber+' 页';leaf.onclick=()=>go(bookmark.pageNumber);container.append(leaf)}})}append(null,box)}
function applyLayout(){const page=$('page-wrap');page.style.margin='25vh auto'}function setPagePosition(direction){const viewer=$('viewer'),page=$('page-wrap'),viewerRect=viewer.getBoundingClientRect(),pageRect=page.getBoundingClientRect(),edgeOffset=viewer.clientHeight*.25,pageTop=viewer.scrollTop+(pageRect.top-viewerRect.top),pageBottom=pageTop+pageRect.height,target=direction<0?pageBottom-viewer.clientHeight+edgeOffset:pageTop-edgeOffset,maxScroll=Math.max(0,viewer.scrollHeight-viewer.clientHeight);viewer.scrollTop=Math.max(0,Math.min(maxScroll,target))}
function go(n,preserve=false){if(!state.manifest)return;const previousPage=state.page;const targetPage=Math.min(pageCount(),Math.max(1,Number(n)||1));if(!preserve)stop();state.page=targetPage;state.storedView.page=state.page;writeViewState();$('page').value=state.page;const image=$('page-image');const direction=targetPage<previousPage?-1:1;image.onload=()=>{applyLayout();renderOcr();requestAnimationFrame(()=>setPagePosition(direction))};image.src=path(state.page)}
function normalizeSearch(value){return String(value||'').replace(/[\s]+/g,' ').trim().toLowerCase()}function updateSearch(){const input=$('ocr-search'),mode=$('ocr-search-mode'),count=$('ocr-search-count');if(!input)return;const q=normalizeSearch(input.value);state.searchResults=q?state.manifest.ocrRecords.filter(r=>normalizeSearch(mode.value==='title'?r.title:r.text).includes(q)):[];state.searchIndex=-1;count.textContent=state.searchResults.length?'0 / '+state.searchResults.length:'';renderOcr()}function moveSearch(step){if(!state.searchResults.length)return;state.searchIndex=(state.searchIndex+step+state.searchResults.length)%state.searchResults.length;const record=state.searchResults[state.searchIndex];$('ocr-search-count').textContent=(state.searchIndex+1)+' / '+state.searchResults.length;if(state.page!==record.pageNumber)go(record.pageNumber,true);else{renderOcr();document.querySelector('.ocr.current')?.scrollIntoView({block:'center',inline:'center'})}}function previousSearch(){moveSearch(-1)}function nextSearch(){moveSearch(1)}
function renderOcr(){const layer=$('ocr-layer'),img=$('page-image'),wrap=$('page-wrap');layer.replaceChildren();const scale=state.zoom,base=state.manifest.pageScale||1;const width=img.naturalWidth*scale,height=img.naturalHeight*scale;img.style.width=width+'px';wrap.style.width=width+'px';wrap.style.height=height+'px';const records=state.manifest.ocrRecords.filter(x=>x.pageNumber===state.page);records.forEach(r=>{const x=document.createElement('div');x.className='ocr';x.hidden=!state.showOcr;if(r.id===state.searchResults[state.searchIndex]?.id)x.classList.add('current');const z=r.captureZoom||1;x.style.left=(r.x/z*base*scale)+'px';x.style.top=(r.y/z*base*scale)+'px';x.style.width=(r.width/z*base*scale)+'px';x.style.height=(r.height/z*base*scale)+'px';const label=document.createElement('div');label.className='ocr-label';label.textContent=r.title||r.text;const line=document.createElement('div');line.className='ocr-line';const box=document.createElement('div');box.className='ocr-box';if(r.audioPath){const b=document.createElement('button');b.textContent='播放';b.onclick=e=>{e.stopPropagation();play([r])};box.append(b)}box.append(label,line);x.append(box);layer.append(x)});layer.style.width=width+'px';layer.style.height=height+'px'}
function play(items,continuous=false){state.playing=items.filter(x=>x.audioPath);state.index=0;state.continuous=continuous;nextAudio()}function nextAudio(){if(state.index>=state.playing.length&&state.continuous&&state.page<pageCount()){const next=state.page+1;state.playing.push(...state.manifest.ocrRecords.filter(x=>x.pageNumber===next&&x.audioPath));go(next,true)}const r=state.playing[state.index++];if(!r){stop();return}if(r.pageNumber!==state.page)go(r.pageNumber,true);audio.src=r.audioPath;audio.play();$('pause').hidden=false;$('stop').hidden=false;$('pause').textContent='暂停播放'}function stop(){audio.pause();audio.removeAttribute('src');state.playing=[];state.index=0;state.continuous=false;$('pause').hidden=true;$('stop').hidden=true}
function setZoom(value){state.zoom=Math.min(3,Math.max(.25,Number(value)||1));$('zoom').value=Math.round(state.zoom*100)+'%';renderOcr()}function resetWheel(){state.wheelTravel=0;state.wheelDirection=0}function handleBoundaryWheel(e){const viewer=$('viewer'),atTop=viewer.scrollTop<=1,atBottom=viewer.scrollTop+viewer.clientHeight>=viewer.scrollHeight-1;if(!atTop&&!atBottom){resetWheel();return}const direction=e.deltaY<0?-1:1;if((direction<0&&!atTop)||(direction>0&&!atBottom)){resetWheel();return}if(state.wheelDirection!==direction){state.wheelDirection=direction;state.wheelTravel=0}state.wheelTravel+=Math.abs(e.deltaY);if(state.wheelTravel>=180&&!state.wheelLocked){state.wheelLocked=true;resetWheel();go(state.page+direction);setTimeout(()=>state.wheelLocked=false,260)}}audio.onended=nextAudio;$('previous').onclick=()=>go(state.page-1);$('next').onclick=()=>go(state.page+1);$('page').onchange=e=>go(e.target.value);$('zoom-out').onclick=()=>setZoom(state.zoom-.1);$('zoom-in').onclick=()=>setZoom(state.zoom+.1);$('zoom').onchange=e=>setZoom(String(e.target.value).replace('%','')/100);$('toggle-ocr').onclick=()=>{state.showOcr=!state.showOcr;$('toggle-ocr').textContent=state.showOcr?'隐藏 OCR':'显示 OCR';renderOcr()};$('read-page').onclick=()=>play(state.manifest.ocrRecords.filter(x=>x.pageNumber===state.page),false);$('read-all').onclick=()=>play(state.manifest.ocrRecords.filter(x=>x.pageNumber>=state.page),true);$('pause').onclick=()=>{if(audio.paused){audio.play();$('pause').textContent='暂停播放'}else{audio.pause();$('pause').textContent='继续播放'}};$('stop').onclick=stop;$('viewer').addEventListener('wheel',handleBoundaryWheel,{passive:true});window.onresize=renderOcr;
""";

    private const string AudioControlsScript = """
const mobileBookmarkButton=document.createElement('button');mobileBookmarkButton.id='toggle-bookmarks';mobileBookmarkButton.textContent='书签';mobileBookmarkButton.title='显示/隐藏书签';document.querySelector('header').prepend(mobileBookmarkButton);mobileBookmarkButton.onclick=()=>document.body.classList.toggle('bookmarks-open');
function closeMobileBookmarks(){if(window.matchMedia('(max-width:720px)').matches)document.body.classList.remove('bookmarks-open')}
$('bookmarks').addEventListener('click',event=>{if(event.target.closest('.bookmark-leaf'))closeMobileBookmarks()});
document.addEventListener('pointerdown',event=>{if(window.matchMedia('(max-width:720px)').matches&&document.body.classList.contains('bookmarks-open')&&!event.target.closest('aside,#toggle-bookmarks'))closeMobileBookmarks()});
function fitPageToViewport(){if(state.manualZoom||!window.matchMedia('(max-width:720px)').matches)return;const viewer=$('viewer'),image=$('page-image');if(!image.naturalWidth||!viewer.clientWidth)return;const availableWidth=Math.max(1,viewer.clientWidth-16);state.zoom=Math.min(3,Math.max(.25,availableWidth/image.naturalWidth));$('zoom').value=Math.round(state.zoom*100)+'%'}
const originalSetZoom=setZoom;setZoom=function(value){state.manualZoom=true;originalSetZoom(value)};const pageImage=$('page-image');pageImage.addEventListener('load',()=>{if(!state.manualZoom){fitPageToViewport();renderOcr()}});window.addEventListener('resize',()=>{if(!state.manualZoom){fitPageToViewport();renderOcr()}});
const pinchViewer=$('viewer');pinchViewer.addEventListener('touchstart',event=>{if(event.touches.length===2){const dx=event.touches[0].clientX-event.touches[1].clientX,dy=event.touches[0].clientY-event.touches[1].clientY;state.pinchStartDistance=Math.hypot(dx,dy);state.pinchStartZoom=state.zoom}},{passive:true});pinchViewer.addEventListener('touchmove',event=>{if(state.pinchStartDistance>0&&event.touches.length===2){event.preventDefault();const dx=event.touches[0].clientX-event.touches[1].clientX,dy=event.touches[0].clientY-event.touches[1].clientY;setZoom(state.pinchStartZoom*Math.hypot(dx,dy)/state.pinchStartDistance)}},{passive:false});pinchViewer.addEventListener('touchend',event=>{if(event.touches.length<2)state.pinchStartDistance=0},{passive:true});
const audioStatus=$('audio-status'),audioProgress=$('audio-progress'),audioTime=$('audio-time');
let prefetchAudios=[];
function clearPrefetch(){prefetchAudios.forEach(item=>{item.src='';item.load()});prefetchAudios=[]}
function prefetchNextPage(){clearPrefetch();if(!state.continuous||!state.manifest||state.page>=pageCount())return;const nextPage=state.page+1;state.manifest.ocrRecords.filter(record=>record.pageNumber===nextPage&&record.audioPath).forEach(record=>{const item=new Audio();item.preload='auto';item.src=record.audioPath;prefetchAudios.push(item)})}
const originalNextAudio=nextAudio,originalPlay=play,originalStop=stop;
nextAudio=function(){originalNextAudio();prefetchNextPage()};
play=function(items,continuous=false){originalPlay(items,continuous);if(continuous)prefetchNextPage()};
stop=function(){originalStop();clearPrefetch()};
audio.onended=nextAudio;
function formatAudioTime(value){if(!Number.isFinite(value)||value<0)return '00:00';const seconds=Math.floor(value),minutes=Math.floor(seconds/60),remainder=seconds%60;return String(minutes).padStart(2,'0')+':'+String(remainder).padStart(2,'0')}
function updateAudioTime(){const duration=audio.duration;audioTime.textContent=formatAudioTime(audio.currentTime)+' / '+formatAudioTime(duration);audioProgress.value=Number.isFinite(duration)&&duration>0?String(audio.currentTime/duration*100):'0'}
function setAudioStatus(value){audioStatus.textContent=value}
audio.addEventListener('loadstart',()=>setAudioStatus('加载中'));
audio.addEventListener('waiting',()=>setAudioStatus('缓冲中'));
audio.addEventListener('stalled',()=>setAudioStatus('缓冲中'));
audio.addEventListener('loadedmetadata',updateAudioTime);
audio.addEventListener('canplay',()=>{if(!audio.paused)setAudioStatus('播放中')});
audio.addEventListener('playing',()=>setAudioStatus('播放中'));
audio.addEventListener('pause',()=>{if(audio.currentTime>0&&audio.currentTime<audio.duration)setAudioStatus('已暂停')});
audio.addEventListener('timeupdate',updateAudioTime);
audio.addEventListener('ended',()=>{setAudioStatus('播放完成');updateAudioTime()});
audio.addEventListener('emptied',()=>{setAudioStatus('未播放');updateAudioTime()});
audio.addEventListener('error',()=>setAudioStatus('加载失败'));
audioProgress.addEventListener('input',()=>{if(Number.isFinite(audio.duration)&&audio.duration>0)audio.currentTime=Number(audioProgress.value)/100*audio.duration});
""";
}
