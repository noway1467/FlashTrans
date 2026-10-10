using System.IO;
using System.Net;
using System.Text;

namespace FlashTrans.Services;

public sealed partial class TranslationDocument
{
    /// <summary>独立、无脚本的阅读副本；不执行原文 HTML，不伪装成原格式的无损导出。</summary>
    void WriteBilingual(Stream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        static string Escape(string value) => WebUtility.HtmlEncode(value);
        writer.Write("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        writer.Write("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>双语对照</title>");
        writer.Write("<style>body{font-family:system-ui,'Microsoft YaHei',sans-serif;max-width:1100px;margin:32px auto;padding:0 20px;color:#243047;background:#fafbfc}h1{font-size:24px}header{margin-bottom:28px}section{display:grid;grid-template-columns:1fr 1fr;gap:24px;padding:18px 0;border-top:1px solid #dbe1e8;break-inside:avoid}p{margin:0;white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.85}.target{color:#173c60}small{color:#596579}@media(max-width:650px){section{grid-template-columns:1fr}}@media print{body{background:white;margin:0;font-size:11pt}}</style>");
        writer.Write("<body><header><h1>" + Escape(Path.GetFileName(SourcePath)) + "</h1><small>原文 / 译文 · 正文分段对照，不保留原文件的图片、表格与版式</small></header><main>");
        foreach (var unit in Units)
        {
            writer.Write("<section><p dir=\"auto\">" + Escape(string.Join(" ", unit.Parts.Select(p => p.Text))) +
                "</p><p class=\"target\" dir=\"auto\" lang=\"" + Escape(TargetLanguage) + "\">" + Escape(string.Join(" ", unit.Result!)) + "</p></section>");
        }
        writer.Write("</main></body></html>");
    }
}
