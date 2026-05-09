using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RecipeApp
{
    public partial class MainWindow : Window
    {
        private readonly RecipeService _recipeService = new();
        private string SavePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedRecipes");
        // これが不足しているために CS0103 が発生しています
        private static readonly System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();

        public MainWindow()
        {
            InitializeComponent();
            // User-AgentがないとWikipedia APIに拒否されるので注意
            client.DefaultRequestHeaders.Add("User-Agent", "RecipeApp/1.0");
        }
        private async void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            string query = txtQuery.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            SetLoadingState(true);

            // 1. Wikibooks -> 2. Wikipedia の順で検索 (物流のフォールバックと同じ思想)
            // 修正後（RecipeService インスタンスを経由する）
            string json = await _recipeService.FetchWikiJsonAsync(query, true);
            if (json == null || !IsPageFound(json))
            {
                json = await _recipeService.FetchWikiJsonAsync(query, false);
            }

            if (json != null && IsPageFound(json))
            {
                UpdateUIWithRecipe(json, query);
            }
            else
            {
                txtResult.Text = $"「{query}」に一致する情報は確保できませんでした。";
            }

            SetLoadingState(false);
        }

        private void UpdateUIWithRecipe(string json, string query)
        {
            using var doc = JsonDocument.Parse(json);
            var page = doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject().First().Value;

            string fullText = page.GetProperty("extract").GetString() ?? "";

            // セクション抽出のロジックを構造化
            string ingredients = ExtractSection(fullText, "材料", "具材") ?? "(材料情報なし)";
            string steps = ExtractSection(fullText, "作り方", "調理法", "手順") ?? "(手順情報なし)";

            lblRecipeTitle.Text = query;
            txtResult.Text = $"■ 材料\n{ingredients}\n\n■ 作り方\n{steps}";

            // 画像処理
            if (page.TryGetProperty("original", out var imgEl))
            {
                imgRecipe.Source = new BitmapImage(new Uri(imgEl.GetProperty("source").GetString()));
                imgRecipe.Visibility = Visibility.Visible;
            }
            else
            {
                imgRecipe.Visibility = Visibility.Collapsed;
            }
        }

        // 構造化されたセクション抽出 (正規表現を活用)
        private string? ExtractSection(string text, params string[] keywords)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            bool inSection = false;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();

                // 見出し行の検知 (例: == 材料 ==)
                if (trimmed.StartsWith("=") && trimmed.EndsWith("="))
                {
                    if (keywords.Any(k => trimmed.Contains(k)))
                    {
                        inSection = true;
                        continue;
                    }
                    else if (inSection)
                    {
                        break; // 次の見出しが来たら終了
                    }
                }

                if (inSection && !string.IsNullOrWhiteSpace(trimmed))
                {
                    // ロジスティクス的な「リストの正規化」
                    string bullet = trimmed.TrimStart('*', '#', ':', ' ', '　');
                    sb.AppendLine($" ・ {bullet}");
                }
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        private void SetLoadingState(bool isLoading)
        {
            txtResult.Text = isLoading ? "情報を抽出中..." : txtResult.Text;
            btnSearch.IsEnabled = !isLoading;
        }
    


        private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshHistoryList();

        

        // ページが存在するかチェックする補助メソッド
        private bool IsPageFound(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            var firstPage = pages.EnumerateObject().FirstOrDefault().Value;

            return !(firstPage.TryGetProperty("missing", out _) ||
                    (firstPage.TryGetProperty("pageid", out var id) && id.GetInt32() == -1));
        }
        // 2. JSON解析と表示
        private void ParseWikiJson(string json, string query)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("query", out var queryEl)) return;

                var pages = queryEl.GetProperty("pages");
                var firstPage = pages.EnumerateObject().FirstOrDefault().Value;

                // ページが見つからない場合の処理
                if (firstPage.TryGetProperty("missing", out _) ||
                   (firstPage.TryGetProperty("pageid", out var id) && id.GetInt32() == -1))
                {
                    txtResult.Text = $"「{query}」のレシピは見つかりませんでした。\n料理名が正しいか確認してください。";
                    return;
                }

                // テキスト抽出
                if (firstPage.TryGetProperty("extract", out var extractEl))
                {
                    string full = extractEl.GetString() ?? "";
                    string? rawIngredients = GetSectionText(full, "材料") ?? GetSectionText(full, "具材");
                    string? rawSteps = GetSectionText(full, "作り方") ?? GetSectionText(full, "調理法") ?? GetSectionText(full, "手順");

                    lblRecipeTitle.Text = query;
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("■ 材料");
                    sb.AppendLine(rawIngredients != null ? FormatAsList(rawIngredients) : "(材料セクションなし)");
                    sb.AppendLine();
                    sb.AppendLine("■ 作り方");
                    sb.AppendLine(rawSteps != null ? FormatAsList(rawSteps) : "(手順セクションなし)");

                    txtResult.Text = sb.ToString();
                }

                // 画像抽出
                if (firstPage.TryGetProperty("original", out var imgEl))
                {
                    imgRecipe.Source = new BitmapImage(new Uri(imgEl.GetProperty("source").GetString()));
                    imgRecipe.Visibility = Visibility.Visible;
                }
                else
                {
                    imgRecipe.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                txtResult.Text = $"解析エラー: {ex.Message}";
            }
        }

        // 3. 保存ボタン
        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(lblRecipeTitle.Text)) return;
            if (!Directory.Exists(SavePath)) Directory.CreateDirectory(SavePath);

            string safeName = string.Join("_", lblRecipeTitle.Text.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllText(Path.Combine(SavePath, safeName + ".txt"), txtResult.Text);

            MessageBox.Show("保存しました。");
            RefreshHistoryList();
        }

        // 4. 履歴選択
        private void lstHistory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (lstHistory.SelectedItem == null) return;
            string path = Path.Combine(SavePath, lstHistory.SelectedItem.ToString() + ".txt");
            if (File.Exists(path))
            {
                lblRecipeTitle.Text = lstHistory.SelectedItem.ToString();
                txtResult.Text = File.ReadAllText(path);
                imgRecipe.Visibility = Visibility.Collapsed;
            }
        }

        // --- ユーティリティ ---
        private void RefreshHistoryList()
        {
            if (!Directory.Exists(SavePath)) return;
            lstHistory.ItemsSource = Directory.GetFiles(SavePath, "*.txt").Select(Path.GetFileNameWithoutExtension).ToList();
        }

        private string? GetSectionText(string text, string section)
        {
            string[] lines = text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
            int startLine = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                // 改良：先頭が「=」で始まり、かつキーワード（材料/作り方など）が含まれる行を探す
                // これにより ==材料== も ===玉子焼きの材料=== も両方ヒットします
                if (line.StartsWith("=") && line.Contains(section))
                {
                    startLine = i;
                    break;
                }
            }

            if (startLine == -1) return null;

            StringBuilder sb = new StringBuilder();
            for (int i = startLine + 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                // 次の見出し（同じレベルかそれ以上の見出し）が来たら終了
                // ただし、空行や単なる飾りは無視して中身を拾い続ける
                if (line.StartsWith("==") && !line.Contains(section)) break;

                if (!string.IsNullOrWhiteSpace(line))
                {
                    // Wikipedia特有の記号（* や #）をリスト形式っぽく整える
                    string cleanedLine = line.TrimStart('*', '#', ' ', '　');
                    sb.AppendLine(" ・ " + cleanedLine);
                }
            }

            return sb.ToString().Trim();
        }

        private string FormatAsList(string raw)
        {
            var sb = new StringBuilder();
            foreach (var line in raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                if (line.StartsWith("*") || line.StartsWith("#") || char.IsDigit(line[0]))
                    sb.AppendLine(" ・ " + line.TrimStart('*', '#', ' '));
            }
            return sb.Length > 0 ? sb.ToString() : raw;
        }
    }
    // このクラスがないために CS0246 エラーが発生しています
    public class RecipeService
    {
        private static readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        static RecipeService()
        {
            // Wikipedia APIを利用する際は User-Agent の設定が必須です
            if (!_client.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _client.DefaultRequestHeaders.Add("User-Agent", "RecipeApp/2.0");
            }
        }

        public async Task<string?> FetchWikiJsonAsync(string query, bool isWikibooks = true)
        {
            string baseUrl = isWikibooks ? "ja.wikibooks.org" : "ja.wikipedia.org";
            string title = isWikibooks ? $"料理本/{query}" : query;
            string url = $"https://{baseUrl}/w/api.php?action=query&format=json&prop=extracts|pageimages&explaintext=1&piprop=original&titles={Uri.EscapeDataString(title)}";

            try
            {
                var response = await _client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
                return null;
            }
            catch (Exception)
            {
                // 通信エラー（オフラインなど）の場合
                return null;
            }
        }
    }
}
