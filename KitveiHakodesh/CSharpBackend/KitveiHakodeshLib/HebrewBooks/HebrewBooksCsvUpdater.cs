using HtmlAgilityPack;
using KitveiHakodeshLib.Settings;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KitveiHakodeshLib.HebrewBooks
{
    /// <summary>
    /// Incrementally updates KitveiHakodesh\HebrewBooks.csv once a month.
    /// Uses HttpClient + HtmlAgilityPack to scrape beta.hebrewbooks.org.
    /// Last-run date is stored in the registry via AppSettings.
    /// Stops after MAX_CONSECUTIVE_EMPTY consecutive IDs with no data.
    /// </summary>
    public class HebrewBooksCsvUpdater
    {
        private const string BaseUrl = "https://beta.hebrewbooks.org";
        private const int MaxConsecutiveEmpty = 10;
        private const int RequestDelayMs = 1000;
        private const int UpdateIntervalDays = 30;

        // bin\Debug\KitveiHakodesh\HebrewBooks.csv — the folder the Vue app is served from
        private static readonly string CsvPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KitveiHakodesh", "HebrewBooks.csv");

        private static readonly string BackupDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KitveiHakodesh", "backups");

        private static readonly HttpClient Http = new HttpClient();

        static HebrewBooksCsvUpdater()
        {
            Http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        }

        /// <summary>
        /// Call at app startup. Fires-and-forgets the update if 30+ days have elapsed.
        /// </summary>
        public void RunIfDue()
        {
            DateTime lastRun = AppSettings.LoadHbCsvLastUpdated();
            if ((DateTime.UtcNow - lastRun).TotalDays < UpdateIntervalDays) return;

            Log("Due (last: " + (lastRun == DateTime.MinValue ? "never" : lastRun.ToString("yyyy-MM-dd")) + "). Starting.");
            Task.Run(() => RunUpdate());
        }

        // ── Core loop ─────────────────────────────────────────────────────────

        private async Task RunUpdate()
        {
            string backupPath = CreateBackup();
            int startId = GetMaxIdFromCsv() + 1;
            int currentId = startId;
            int consecutiveEmpty = 0;
            int added = 0;

            Log("Starting from ID " + startId);

            try
            {
                while (consecutiveEmpty < MaxConsecutiveEmpty)
                {
                    BookRow book = await FetchBook(currentId);

                    if (book != null)
                    {
                        AppendRow(book);
                        added++;
                        consecutiveEmpty = 0;
                        Log("+ " + currentId + " " + book.Title);
                    }
                    else
                    {
                        consecutiveEmpty++;
                        Log("- " + currentId + " (empty " + consecutiveEmpty + "/" + MaxConsecutiveEmpty + ")");
                    }

                    currentId++;
                    await Task.Delay(RequestDelayMs);
                }

                AppSettings.SaveHbCsvLastUpdated(DateTime.UtcNow);
                Log("Done. Added " + added + " books. Last ID checked: " + (currentId - 1));
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                if (backupPath != null && File.Exists(backupPath))
                {
                    try { File.Copy(backupPath, CsvPath, overwrite: true); Log("Backup restored."); } catch { }
                }
            }
        }

        // ── Scraping ──────────────────────────────────────────────────────────

        private static async Task<BookRow> FetchBook(int bookId)
        {
            string html;
            try
            {
                html = await Http.GetStringAsync(BaseUrl + "/" + bookId);
            }
            catch (Exception ex)
            {
                Log("HTTP error for " + bookId + ": " + ex.Message);
                return null;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            string title  = GetText(doc, "cpMstr_lblHebSefername");
            string author = GetText(doc, "cpMstr_lblHebAuth");
            string place  = GetText(doc, "cpMstr_lblHebPlace");
            string year   = GetText(doc, "cpMstr_lblHebDate");
            string pages  = GetText(doc, "cpMstr_lblPages");
            string tags   = GetTags(doc, "cpMstr_pnltag");

            if (title == "" && author == "" && place == "" && year == "" && pages == "" && tags == "")
                return null;

            return new BookRow
            {
                Id     = bookId,
                Title  = CsvEscape(title),
                Author = CsvEscape(author),
                Place  = place,
                Year   = year,
                Pages  = pages,
                Tags   = tags,
            };
        }

        private static string GetText(HtmlDocument doc, string id)
        {
            var node = doc.GetElementbyId(id);
            if (node == null) return "";
            return HtmlEntity.DeEntitize(node.InnerText).Trim().Replace("\n", " ");
        }

        private static string GetTags(HtmlDocument doc, string containerId)
        {
            var container = doc.GetElementbyId(containerId);
            if (container == null) return "";

            var sb = new StringBuilder();
            foreach (var tag in container.SelectNodes(".//span[@class='tag'] | .//*[contains(@class,'tag')]") ?? new HtmlNodeCollection(null))
            {
                string t = HtmlEntity.DeEntitize(tag.InnerText).Trim();
                if (t == "") continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(t);
            }
            return sb.ToString();
        }

        // ── CSV helpers ───────────────────────────────────────────────────────

        private static int GetMaxIdFromCsv()
        {
            if (!File.Exists(CsvPath)) return 0;
            int max = 0;
            foreach (string line in File.ReadLines(CsvPath, Encoding.UTF8))
            {
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (int.TryParse(line.Substring(0, comma), out int id) && id > max)
                    max = id;
            }
            return max;
        }

        private static string CreateBackup()
        {
            if (!File.Exists(CsvPath)) return null;
            Directory.CreateDirectory(BackupDir);
            string dest = Path.Combine(BackupDir, "HebrewBooks_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
            File.Copy(CsvPath, dest, overwrite: true);
            return dest;
        }

        private static void AppendRow(BookRow b)
        {
            string line = b.Id + "," + b.Title + "," + b.Author + "," + b.Place + "," + b.Year + "," + b.Pages + "," + b.Tags + "\n";
            File.AppendAllText(CsvPath, line, Encoding.UTF8);
        }

        private static string CsvEscape(string s) => s.Replace(",", " -");

        private static void Log(string msg) => System.Diagnostics.Debug.WriteLine("[HbCsvUpdater] " + msg);

        // ── Model ─────────────────────────────────────────────────────────────

        private class BookRow
        {
            public int Id;
            public string Title, Author, Place, Year, Pages, Tags;
        }
    }
}
