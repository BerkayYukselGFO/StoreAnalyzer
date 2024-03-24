using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.Support;
using SeleniumExtras.WaitHelpers;
using OpenQA.Selenium.DevTools;
using System.Net;
using FireSharp;
using FireSharp.Config;
using FireSharp.Interfaces;
using FireSharp.Response;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Threading;

namespace AppAnalyzer
{
    public class Developer
    {
        public string developerName;
        public List<Game> games = new List<Game>();
        public DateTime lastCheckDate;
    }
    public class DeveloperLink
    {
        public string developerName;
        public string developerLink;
        public int linkType;
    }
    public class Game
    {
        public string gameBundleID;
        public string gameName;
        public List<long> downloadValues = new List<long>();
        public List<DateTime> controlTimes = new List<DateTime>();
        public DateTime releaseDate;
        public string iconLink;
    }

    class Program
    {
        public static List<Developer> developers;

        private static List<DeveloperLink> developerLinks = new List<DeveloperLink>();
        public static List<TimeSpan> timeSpans = new List<TimeSpan>();

        public static async Task Main(string[] args)
        {
            var startTime = DateTime.Now;
            await GetCurrentData();

            for (int i = 0; i < developerLinks.Count; i++)
            {
                await CheckDeveloper(developerLinks[i]);
            }
            var endTime = DateTime.Now;
            var diff = endTime - startTime;
            Console.WriteLine("Tarama Bitti geçen süre: " + diff);
            Console.Read();
        }

        public static async Task GetCurrentData()
        {

            var config = new FirebaseConfig
            {
                AuthSecret = "dHpH5Sh077h0l9HfFCmkI3QSGAmmalbaiWMXP1QM",
                BasePath = "https://storeanalyzer-e62ee-default-rtdb.europe-west1.firebasedatabase.app/"
            };
            IFirebaseClient client;

            client = new FireSharp.FirebaseClient(config);

            // Eger hata var ise null doner
            if (client == null)
                Console.WriteLine("Bağlantı hatasi.");


            FirebaseResponse response = await client.GetAsync("Developer");


            if (response.Body != null)
            {
                var developerData = JsonConvert.DeserializeObject<Dictionary<string, Developer>>(response.Body);
                if (developerData != null)
                {
                    developers = developerData.Values.ToList();
                    Console.WriteLine("Mevcut developer Veriler Getirildi!");
                }
                else
                {
                    developers = new List<Developer>();
                    Console.WriteLine("Mevcut developer data yok!");
                }

            }

            response = await client.GetAsync("DeveloperLinks");
            if (response.Body != null)
            {
                var developerLinkData = JsonConvert.DeserializeObject<List<DeveloperLink>>(response.Body);
                if (developerLinkData != null)
                {
                    developerLinks = developerLinkData.ToList();
                    Console.WriteLine("Mevcut developerLink Veriler Getirildi!");
                }
                else
                {
                    developerLinkData = new List<DeveloperLink>();
                    Console.WriteLine("Mevcut developerlink data yok!");
                }

            }
        }

        public static async Task CheckDeveloper(DeveloperLink developerLink)
        {
            if (developers.Any(x => x.developerName == developerLink.developerName))
            {
                if (DateTime.Compare(developers.First(x => x.developerName == developerLink.developerName).lastCheckDate.AddDays(1), DateTime.Now) > 0)
                {
                    Console.WriteLine("Developer name: " + developerLink.developerName + " Son test edilmesinden bu yana 1 gün geçmemiş!");
                    return;
                }
            }

            var bundleIds = new List<string>();
            if (developerLink.linkType == 0)
            {
                bundleIds = CheckAppFromDeveloperStorePage(developerLink.developerLink);
            }
            else if (developerLink.linkType == 1)
            {
                bundleIds = CheckAppFromDeveloperMoreAppPage(developerLink.developerLink);
            }
            if (bundleIds.Count == 0) return;

            var semaphore = new SemaphoreSlim(20); // Aynı anda maksimum 10 görev için izin ver.

            var tasks = new List<Task<Game>>();

            var gameDatas = new List<Game>();
            if (developers.Any(x => x.developerName == developerLink.developerName))
            {
                var developer = developers.Where(x => x.developerName == developerLink.developerName).First();

                foreach (var bundleId in bundleIds)
                {
                    await semaphore.WaitAsync(); // Semaforun bir ünitesini al, yer yoksa beklet.
                    Task<Game> task;
                    if (developer.games.Any(x => x.gameBundleID == bundleId))
                    {
                        var currentGameData = developer.games.Where(x => x.gameBundleID == bundleId).First();
                        task = CheckGame(bundleId, currentGameData);
                    }
                    else
                    {
                        task = CheckGame(bundleId);
                    }

                    tasks.Add(task.ContinueWith(t =>
                    {
                        semaphore.Release(); // Görev bittiğinde semaforun bir ünitesini geri ver.
                        return t.Result;
                    }));
                }
            }
            else
            {
                foreach (var bundleId in bundleIds)
                {
                    await semaphore.WaitAsync(); // Semaforun bir ünitesini al, yer yoksa beklet.
                    var task = CheckGame(bundleId);
                    tasks.Add(task.ContinueWith(t =>
                    {
                        semaphore.Release(); // Görev bittiğinde semaforun bir ünitesini geri ver.
                        return t.Result;
                    }));
                }
            }

            // Mevcut gruptaki tüm görevlerin tamamlanmasını bekle
            var completedTasks = await Task.WhenAll(tasks);
            gameDatas.AddRange(completedTasks);
            tasks.Clear();

            if (developers.Any(x => x.developerName == developerLink.developerName)) //eğer geliştirici varsa
            {
                var developer = developers.Where(x => x.developerName == developerLink.developerName).First();
                developer.games = gameDatas;
                developer.lastCheckDate = DateTime.Now;
                await SaveDeveloper(developer);
            }
            else
            {
                var newDeveloper = new Developer();
                newDeveloper.developerName = developerLink.developerName;
                newDeveloper.lastCheckDate = DateTime.Now;
                newDeveloper.games = gameDatas;
                await SaveDeveloper(newDeveloper);
            }

        }


        public static List<string> CheckAppFromDeveloperMoreAppPage(string developerLink)
        {
            // Chrome WebDriver'ı başlat
            var driver = new ChromeDriver();

            // Web sayfasını aç

            driver.Navigate().GoToUrl(developerLink);
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);

            while (true)
            {
                IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
                var currentScrollHeight = (long)js.ExecuteScript("return document.body.scrollHeight");
                js.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                System.Threading.Thread.Sleep(1000);
                var newScrollHeight = (long)js.ExecuteScript("return document.body.scrollHeight");

                if (newScrollHeight == currentScrollHeight)
                {
                    break;
                }
            }

            // Sayfanın HTML içeriğini al
            string htmlContent = driver.PageSource;

            // HtmlAgilityPack kullanarak HTML içeriğini analiz et
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(htmlContent);

            // İstenilen öğeleri seçin
            var appNodes = document.DocumentNode.SelectNodes("//div[@class='ULeU3b']");

            if (appNodes == null)
            {
                driver.Quit();
                return new List<string>();
            }

            var bundleIdList = new List<string>();
            // Seçilen öğeleri yazdırın
            if (appNodes != null)
            {
                foreach (var appNode in appNodes)
                {
                    var appNameNode = appNode.SelectSingleNode(".//div[@class='Epkrse ']");
                    var appName = appNameNode?.InnerText;
                    var appLinkNode = appNode.SelectSingleNode(".//a[@class='Si6A0c ZD8Cqc']");
                    var appLink = "https://play.google.com" + appLinkNode?.GetAttributeValue("href", "");
                    Console.WriteLine("AppName: " + appName + " " + "   bundleID: " + GetBundleIdFromUrl(appLink));
                    bundleIdList.Add(GetBundleIdFromUrl(appLink));
                }
            }
            Console.WriteLine("-------------");
            Console.WriteLine("Uygulama sayisi: " + appNodes.Count());
            Console.WriteLine("-------------");
            driver.Quit();
            return bundleIdList;
        }

        public static List<string> CheckAppFromDeveloperStorePage(string developerLink)
        {
            var driver = new ChromeDriver();

            // Web sayfasını aç
            driver.Navigate().GoToUrl(developerLink);

            // Sayfanın tamamlanması için bir süre bekleyin (JavaScript kodlarının çalışmasını sağlamak için)
            System.Threading.Thread.Sleep(500);


            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(1));
            while (true)
            {
                IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
                js.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                System.Threading.Thread.Sleep(500);
                js.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");

                try
                {
                    var showMoreButton = wait.Until(ExpectedConditions.ElementToBeClickable(By.XPath("//div[@class='VfPpkd-dgl2Hf-ppHlrf-sM5MNb']//span[text()='Show more']")));
                    js.ExecuteScript("arguments[0].scrollIntoView(true);", showMoreButton);
                    js.ExecuteScript("window.scrollBy(0, -window.innerHeight / 2);"); // Ek olarak, biraz yukarı kaydır

                    OpenQA.Selenium.Interactions.Actions actions = new OpenQA.Selenium.Interactions.Actions(driver);
                    actions.MoveToElement(showMoreButton).Click().Perform();
                }
                catch (WebDriverTimeoutException)
                {
                    break;
                }
                catch (NoSuchElementException)
                {
                    break;
                }
                catch (ElementClickInterceptedException)
                {
                    continue;
                }
            }



            // Sayfanın HTML içeriğini al
            string htmlContent = driver.PageSource;

            // HtmlAgilityPack kullanarak HTML içeriğini analiz et
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(htmlContent);

            // İstenilen öğeleri seçin
            var appNodes = document.DocumentNode.SelectNodes("//div[@class='ULeU3b']");

            if (appNodes == null)
            {
                driver.Quit();
                return new List<string>();
            }
            var bundleIdList = new List<string>();
            // Seçilen öğeleri yazdırın
            if (appNodes != null)
            {
                foreach (var appNode in appNodes)
                {
                    var appName = appNode.SelectSingleNode(".//div[@class='Epkrse ']")?.InnerText;
                    var appLinkNode = appNode.SelectSingleNode(".//a[@class='Si6A0c ZD8Cqc']");
                    var appLink = "https://play.google.com" + appLinkNode?.GetAttributeValue("href", "");
                    Console.WriteLine("AppName: " + appName + " " + "   bundleID: " + GetBundleIdFromUrl(appLink));
                    bundleIdList.Add(GetBundleIdFromUrl(appLink));
                }
            }
            Console.WriteLine("-------------");
            Console.WriteLine("Uygulama sayisi: " + appNodes.Count());
            Console.WriteLine("-------------");
            driver.Quit();
            return bundleIdList;
        }

        public static string GetBundleIdFromUrl(string url)
        {
            // URL'deki '?' işaretinden sonrasını alırız
            int queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                string queryString = url.Substring(queryIndex + 1);

                // '?' işaretinden sonraki parametreleri '&' karakterinden bölerek diziye ayırırız
                string[] parameters = queryString.Split('&');

                // Her bir parametreyi kontrol ederiz
                foreach (string parameter in parameters)
                {
                    // Parametrelerin başlangıcını kontrol ederiz
                    if (parameter.StartsWith("id="))
                    {
                        // '=' işaretinden sonrasını bundle ID olarak alırız
                        return parameter.Substring(3);
                    }
                }
            }

            // Bundle ID bulunamadıysa boş bir değer döndürülür
            return string.Empty;
        }

        public static async Task<Game> CheckGame(string bundleId, Game currentData = null)
        {
            var options = new ChromeOptions();
            options.BinaryLocation = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
            options.AddArgument("--headless=new");
            var startTime = DateTime.Now;
            return await Task.Run(() =>
            {
                using (var driver = new ChromeDriver(options))
                {

                    string url = "https://play.google.com/store/apps/details?id=" + bundleId + "&hl=en_US&gl=US";
                    string encodedUrl = Uri.EscapeUriString(url);
                    driver.Navigate().GoToUrl(encodedUrl);

                    // Sayfanın tamamlanması için bir süre bekleyin (JavaScript kodlarının çalışmasını sağlamak için)
                    System.Threading.Thread.Sleep(50);

                    var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(1));

                    IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
                    while (true)
                    {
                        try
                        {
                            var aboutGameButton = wait.Until(ExpectedConditions.ElementToBeClickable(By.XPath("//button[@class='VfPpkd-Bz112c-LgbsSe yHy1rc eT1oJ QDwDD mN1ivc VxpoF']")));
                            js.ExecuteScript("arguments[0].scrollIntoView(true);", aboutGameButton);
                            js.ExecuteScript("window.scrollBy(0, -window.innerHeight / 2);");

                            OpenQA.Selenium.Interactions.Actions actions = new OpenQA.Selenium.Interactions.Actions(driver);
                            actions.MoveToElement(aboutGameButton).Click().Perform();
                            break;
                        }
                        catch (WebDriverTimeoutException)
                        {
                            break;
                        }
                        catch (NoSuchElementException)
                        {
                            break;
                        }
                        catch (ElementClickInterceptedException)
                        {
                            continue;
                        }
                    }
                    System.Threading.Thread.Sleep(250);
                    // Sayfanın HTML içeriğini al
                    string htmlContent = driver.PageSource;

                    // HtmlAgilityPack kullanarak HTML içeriğini analiz et
                    var document = new HtmlAgilityPack.HtmlDocument();
                    document.LoadHtml(htmlContent);

                    var appName = document.DocumentNode.SelectSingleNode("//h1[@itemprop='name']").InnerText;   //Fd93Bb F5UCq p5VxAd  Fd93Bb ynrBgc xwcR9d Fd93Bb F5UCq p5VxAd
                    if (appName == null)
                    {
                        return currentData;
                    }
                    var downloadCount = ParseDownloadCount(document.DocumentNode.SelectSingleNode("//div[@class='ClM7O' and contains(text(),'+')] ").InnerText);
                  
                    var parentNode = document.DocumentNode.SelectSingleNode(".//div[@class='q078ud' and contains(text(),'Released on')]");
                    if(parentNode == null)
                    {
                        parentNode = document.DocumentNode.SelectSingleNode(".//div[@class='q078ud' and contains(text(),'Updated on')]");
                    }
                    var releaseDate = ParseDateString(parentNode.ParentNode.SelectSingleNode(".//div[@class='reAt0']").InnerText);

                    //var releaseDate = ParseDateString(document.DocumentNode.SelectSingleNode("//div[@class='q078ud' and contains(text(),'Released on')]").ParentNode.SelectSingleNode(".//div[@class='reAt0']").InnerText);//sMUprd

                    HtmlNode iconNode = document.DocumentNode.SelectSingleNode(".//img[@class='T75of QhHVZd']");
                    string iconLink;

                    if (iconNode != null)
                    {
                        iconLink = ResizeImage(iconNode.GetAttributeValue("src", ""), 512);
                    }
                    else
                    {
                        HtmlNode alternativeIconNode = document.DocumentNode.SelectSingleNode(".//img[@class='T75of nm4vBd arM4bb']");

                        if (alternativeIconNode != null)
                        {
                            iconLink = ConvertIconLinkSize(alternativeIconNode.GetAttributeValue("src", ""), 512);
                        }
                        else
                        {
                            // İkinci XPath ifadesi de yoksa veya boşsa, gerekli işlemi yapabilirsiniz.
                            iconLink = "default-icon-url";
                        }
                    }

                    if (currentData == null)
                    {
                        currentData = new Game();
                        currentData.gameName = appName;
                        currentData.downloadValues.Add(downloadCount);
                        currentData.controlTimes.Add(DateTime.Now);
                        currentData.releaseDate = releaseDate;
                        currentData.gameBundleID = bundleId;
                        currentData.iconLink = iconLink;
                    }
                    else
                    {
                        currentData.downloadValues.Add(downloadCount);
                        currentData.controlTimes.Add(DateTime.Now);
                    }
                    Console.WriteLine("Game Name: " + currentData.gameName);
                    Console.WriteLine("Download Count:" + downloadCount);
                    driver.Quit();
                    var endTime = DateTime.Now;
                    timeSpans.Add(endTime - startTime);
                    return currentData;
                }
            });


        }


        public static long ParseDownloadCount(string downloadCount)
        {
            int count = 0;

            if (downloadCount.EndsWith("+"))
            {
                string value = downloadCount.TrimEnd('+');
                if (value.EndsWith("K"))
                {
                    value = value.TrimEnd('K');
                    if (int.TryParse(value, out int intValue))
                    {
                        count = intValue * 1000;
                    }
                }
                else if (value.EndsWith("M"))
                {
                    value = value.TrimEnd('M');
                    if (int.TryParse(value, out int intValue))
                    {
                        count = intValue * 1000000;
                    }
                }
                else if (value.EndsWith("B"))
                {
                    value = value.TrimEnd('B');
                    if (int.TryParse(value, out int intValue))
                    {
                        count = intValue * 1000000000;
                    }
                }
                else
                {
                    if (int.TryParse(value, out int intValue))
                    {
                        count = intValue;
                    }
                }
            }

            return count;
        }

        public static DateTime ParseDateString(string dateString)
        {
            DateTime date;

            if (DateTime.TryParse(dateString, out date))
            {
                return date;
            }
            else
            {
                throw new ArgumentException("Invalid date string format.");
            }
        }

        public static string ResizeImage(string imageUrl, int size)
        {
            string resizedImageUrl = imageUrl.Replace("=s48", $"=s{size}");

            return resizedImageUrl;
        }

        public static string ConvertIconLinkSize(string iconLink, int newSize)
        {
            //int startIndex = iconLink.IndexOf("=w");
            //int endIndex = iconLink.IndexOf("-h");

            //if (startIndex != -1 && endIndex != -1)
            //{
            //    string widthPart = iconLink.Substring(startIndex, endIndex - startIndex);
            //    string newWidthPart = "=w" + newSize.ToString() + "-h" + newSize.ToString();

            //    string resizedIconLink = iconLink.Replace(widthPart, newWidthPart);
            //    return resizedIconLink;
            //}

            return iconLink;
        }

        public static async Task SaveDeveloper(Developer developer)
        {
            var config = new FirebaseConfig
            {
                AuthSecret = "dHpH5Sh077h0l9HfFCmkI3QSGAmmalbaiWMXP1QM",
                BasePath = "https://storeanalyzer-e62ee-default-rtdb.europe-west1.firebasedatabase.app/"
            };
            IFirebaseClient client;

            client = new FireSharp.FirebaseClient(config);

            // Eger hata var ise null doner
            if (client == null)
                Console.WriteLine("Bağlantı hatasi.");


            await client.SetAsync("Developer/" + developer.developerName, developer);
            //for (int i = 0; i < developer.games.Count; i++)
            //{
            //    var setresponce = await client.SetTaskAsync(developer.developerName + "/" + developer.games[i].gameName, developer.games[i]);
            //}
        }
    }
}