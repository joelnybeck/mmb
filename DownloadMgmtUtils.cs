﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using HtmlAgilityPack;
using mmb.ObjectDefinitions;
using MongoDB.Driver;
using ServiceStack.Text;
using MongoDB.Driver.Builders;

namespace mmb
{
    class DownloadMgmtUtils
    {
        //TODO: move download logic and monitor and move methods into this file
        //TODO: add error logging to file specified in app.config and error handling
        //TODO: add support for downloading movies

        //TODO: ERROR HANDLING AND LOGGING

        //Sourced from : https://msdn.microsoft.com/en-us/library/ez801hhe%28v=vs.110%29.aspx
        public static void DownloadFile(string url, string fileName)
        {
            try
            {
                // Create a new WebClient instance.
                WebClient myWebClient = new WebClient();
                myWebClient.Headers.Add("user-agent", ConfigurationManager.AppSettings["user-agent"]);
                //move to log to file
                Log.AppendToLog("Downloading File \"" + fileName + "\" from \"" + url + "\" .......\n",
                    ConfigurationManager.AppSettings["log_file"]);
                // Download the Web resource and save it into the current filesystem folder.
                myWebClient.DownloadFile(url, fileName);
                Log.AppendToLog("Successfully Downloaded File \"" + fileName + "\" from \"" + url + "\" .......\n",
                    ConfigurationManager.AppSettings["log_file"]);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("404"))
                    if (ConfigurationManager.AppSettings["verbose_logging"] == "true")
                        Log.AppendToLog(" Error 404 - Not Found.", ConfigurationManager.AppSettings["log_file"]);
                    else ;
                else
                    Log.AppendToLog(": FATAL File download : " + e + "\n" + url, ConfigurationManager.AppSettings["log_file"]);
            }
        }

        //Updated July 5th
        public static void MonitorAndMove()
        {
            try
            {
                List<Pending> pendingList = MongoUtils.GetMongoCollection
                (
                    @"mongodb://" + ConfigurationManager.AppSettings["mongoHost"] + @"/",
                    ConfigurationManager.AppSettings["port"],
                    ConfigurationManager.AppSettings["db"],
                    ConfigurationManager.AppSettings["pending_collection"]
                ).FindAllAs<Pending>().ToList<Pending>();

                //Get the names of the files in the downloaded folder to compare against those pending 
                List<string> tempS = Directory.GetFiles(ConfigurationManager.AppSettings["file_download_path"]).ToList();
                tempS.AddRange(Directory.GetDirectories(ConfigurationManager.AppSettings["file_download_path"]).ToList());

                foreach (var p in pendingList)
                    foreach (var str in tempS.Where(str => str.Equals(ConfigurationManager.AppSettings["file_download_path"] + @"\" + p.FileName)))
                         if (p.Movie)
                        {
                            MoveFile(str.Replace(@"\\", @"\"), str.Replace(@"\\", @"\").Replace(ConfigurationManager.AppSettings["file_download_path"],
                                    ConfigurationManager.AppSettings["movie_move_path"]));
                            MovieUtils.SetToDownloaded(p.Name);
                            MovieUtils.MoveToDownloaded(p.Name);
                        }
                        else if (p.Show)
                        {
                            MoveFile(str.Replace(@"\\", @"\"), str.Replace(@"\\", @"\").Replace(ConfigurationManager.AppSettings["file_download_path"],
                                ConfigurationManager.AppSettings["show_move_path"]));
                            TvUtils.SetToDownloaded(p.Name, p.Season, p.Episode);
                            TvUtils.MoveToDownloaded(p.Name, p.Season, p.Episode);
                            TvUtils.UpdateCheckMyShow(p.Name, p.Season, p.Episode);
                        }
            }
            catch (Exception e)
            { Log.AppendToLog("Error : FATAL monitor and move. " + e, ConfigurationManager.AppSettings["log_file"]); }
        }

        //Updated December 23rd - Sleep 1 min
        public static void DownloadTorrentFiles()
        {
            List<BasicShow> showsToDownload = TvUtils.GetEpisodesToDownload();
            List<Movie> moviesToDownload = MovieUtils.GetMoviesToDownload();
            TvUtils.SetShowDownloadLocations(showsToDownload);
            DownloadShows(showsToDownload);
            DownloadMovies(moviesToDownload);
            Thread.Sleep(60000);
        }

        //Updated July 24th
        public static void DownloadShows(List<BasicShow> showsToDownload)
        {
            try
            {
                MongoCollection pendingCollection = MongoUtils.GetMongoCollection
                (
                    @"mongodb://" + ConfigurationManager.AppSettings["mongoHost"] + @"/",
                    ConfigurationManager.AppSettings["port"],
                    ConfigurationManager.AppSettings["db"],
                    ConfigurationManager.AppSettings["pending_collection"]
                );

                foreach (var s in showsToDownload.Where(s => !TvUtils.IsInPending(s)))
                {
                    try
                    {
                        //Download torrent file to temp folder first to be able to extract the video 
                        DownloadFile(s.DownloadLocation,
                            ConfigurationManager.AppSettings["temp_torrent_download_path"] + @"\" + s.Name +
                            " S" + s.Season.ToString("00") + "E" + s.Episode.ToString("00") + ".torrent");
                        //Pull video filename out of the torrent file
                        string name = TvUtils.NameFromDownloadString(s.Name, s.Season.ToString("00"),
                            s.Episode.ToString("00"));

                        //int nameIndex = name.IndexOf("FileName");
                        int nameIndex = name.IndexOf(ConfigurationManager.AppSettings["torrent_filename_marker"]);
                        pendingCollection.Insert(new Pending()
                        {
                            FileName = name.Substring(
                                nameIndex + name.Substring(nameIndex + 4, 4).Split(':')[0].Length + 5,
                                Convert.ToInt32(name.Substring(nameIndex + 4, 4).Split(':')[0])),
                            Name = s.Name,
                            Show = true,
                            Movie = false,
                            Episode = s.Episode,
                            Season = s.Season
                        });
                        //Move the torrent file from temp into downloads
                        MoveFile(ConfigurationManager.AppSettings["temp_torrent_download_path"] + @"\" +
                                 s.Name + " S" + s.Season.ToString("00") + "E" + s.Episode.ToString("00") + ".torrent",
                            ConfigurationManager.AppSettings["torrent_download_path"] + @"\" +
                            s.Name + " S" + s.Season.ToString("00") + "E" + s.Episode.ToString("00") + ".torrent");
                        break;
                    }
                    catch (Exception e)
                    { Log.AppendToLog(": FATAL download torrent. " + s.Name + " Season: " + s.Season + " Episode: " + s.Episode, 
                        ConfigurationManager.AppSettings["log_file"]); }
                }
            }
            catch (Exception e)
            { Log.AppendToLog(": FATAL download show. " + e, ConfigurationManager.AppSettings["log_file"]); }
        }

        //Updated July 24th - TODO: Need to accept download quality from user in config
        public static void DownloadMovies(List<Movie> moviesToDownload)
        {
            try
            {
                MongoCollection pendingCollection = MongoUtils.GetMongoCollection
                (
                    @"mongodb://" + ConfigurationManager.AppSettings["mongoHost"] + @"/",
                    ConfigurationManager.AppSettings["port"],
                    ConfigurationManager.AppSettings["db"],
                    ConfigurationManager.AppSettings["pending_collection"]
                );

                foreach (var m in moviesToDownload.Where(s => !MovieUtils.IsInPending(s) && !MovieUtils.IsInDownloaded(s)))
                {
                    var torURL = "";
                    //Is the preferred download quality avilable?
                    foreach (var t in m.DownloadLogistics)
                        if (t.Quality == ConfigurationManager.AppSettings["yts_quality_pref"])
                            torURL = t.TorrentUrl;
                    //Preferred quality was not available. Get next.
                    if (torURL == "")
                        foreach (var t in m.DownloadLogistics)
                            if (t.Quality != ConfigurationManager.AppSettings["yts_quality_excl"])
                                torURL = t.TorrentUrl;
                      
                    //Download torrent file to temp folder first to be able to extract the video filename
                    DownloadFile(torURL,
                        ConfigurationManager.AppSettings["temp_torrent_download_path"] + @"\" + ((m.ImdbTitle == null) ? m.YtsMovieTitle : m.ImdbTitle) + ".torrent");
                    //Pull video filename out of the torrent file
                    string name = MovieUtils.NameFromDownloadString(((m.ImdbTitle == null) ? m.YtsMovieTitle : m.ImdbTitle));
                    List<string> fileNameArry = name.Split(':').ToList();
                    foreach (var f in fileNameArry.Where(f => f.Contains("name")))
                    {
                        name = fileNameArry[fileNameArry.IndexOf(f) + 1];
                        name = name.Substring(0, name.Length - 2);
                        break;
                    }

                    pendingCollection.Insert(new Pending()
                    {
                        FileName = name,
                        Name = m.YtsMovieTitle,
                        Show = false,
                        Movie = true
                    });
                    //Move the torrent file from temp into downloads
                    MoveFile(ConfigurationManager.AppSettings["temp_torrent_download_path"] + @"\" + m.YtsMovieTitle + ".torrent",
                        ConfigurationManager.AppSettings["torrent_download_path"] + @"\" + m.YtsMovieTitle + ".torrent");
                    break;
                }
            }
            catch (Exception e)
            { Log.AppendToLog("Error : FATAL download movie. " + e, ConfigurationManager.AppSettings["log_file"]); }
        }

        //Updated July 15th
        public static HtmlNodeCollection GetHtmlNodeCollection(string url, string nodeDelimiter)
        {
            try
            {
                string htmlString;
                using (WebClient client = new WebClient())
                {
                    //client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)");
                    client.Headers.Add("user-agent", ConfigurationManager.AppSettings["user-agent"]);
                    htmlString = client.DownloadString(url);
                }

                HtmlDocument document = new HtmlDocument();
                document.LoadHtml(htmlString);
                return document.DocumentNode.SelectNodes(nodeDelimiter);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("404"))
                    if (ConfigurationManager.AppSettings["verbose_logging"] == "true")
                        Log.AppendToLog(" Error 404 - Not Found.", ConfigurationManager.AppSettings["log_file"]);
                    else ;
                else
                    Log.AppendToLog(": FATAL Retrieval of HTML Node Collection : " + e + "\n " + url, ConfigurationManager.AppSettings["log_file"]);
                return null;
            }
        }

        //Updated August 2nd
        public static string GetImdbJsonQuery(string url)
        {
            try
            {
                string htmlString;
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("user-agent", ConfigurationManager.AppSettings["user-agent"]); 
                    htmlString = client.DownloadString(url);
                }
                return htmlString;
            }
            catch (Exception e)
            { Log.AppendToLog("Error : FATAL Retrieval of IMDB results : " + url + " " + e, ConfigurationManager.AppSettings["log_file"]); return "Error"; }
        }

        //Updated December 6th
        public static YtsReturn_v2 GetJsonObject(string url)
        {
            try
            {
                string htmlString;
                using (WebClient client = new WebClient())
                { htmlString = client.DownloadString(url); }
                return JsonSerializer.DeserializeFromString<YtsReturnHeaders>(htmlString).data[0];
            }
            catch (Exception e)
            {
                Log.AppendToLog("Error : FATAL Retrieval of YTS json object (" + url + "). " + e, ConfigurationManager.AppSettings["log_file"]);
                return null;
            }
        }

        //Updated Decmeber 6th - updated overwrite logic
        public static void MoveFile(string from, string to)
        {
            try
            {
                if (Directory.Exists(to))
                    Directory.Delete(to);
                else if (File.Exists(to))
                    File.Delete(to); 
                else ;
                File.Move(from, to);
                Log.AppendToLog("Moved file " + from + " to " + to, ConfigurationManager.AppSettings["log_file"]);
            }
            catch (Exception e)
            {
                try
                {
                    Directory.Move(from, to);
                    Log.AppendToLog("Moved directory " + from + " to " + to, ConfigurationManager.AppSettings["log_file"]);
                }
                catch (Exception e2)
                { Log.AppendToLog(" FATAL Moving file : " + e2, ConfigurationManager.AppSettings["log_file"]); }
            }           
        }

        public static void DownloadTorrentFilesThread()
        {
            Console.WriteLine("Download torrents thread started...");
            while (true)
            { DownloadTorrentFiles(); }
        }

        public static void MonitorAndMoveThread()
        {
            Console.WriteLine("Monitor and move thread started...");
            while (true)
            { MonitorAndMove(); }
        }

        //Lifted from Stackoverflow, +1 to asktomsk. Deprecated.
        public static byte[] DownloadFile_old(string url, string file)
        {
            byte[] result;
            byte[] buffer = new byte[4096];

            WebRequest wr = WebRequest.Create(url);
            wr.ContentType = "application/x-bittorrent";
            using (WebResponse response = wr.GetResponse())
            {
                bool gzip = response.Headers["Content-Encoding"] == "gzip";
                var responseStream = gzip
                                        ? new GZipStream(response.GetResponseStream(), CompressionMode.Decompress)
                                        : response.GetResponseStream();

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = responseStream.Read(buffer, 0, buffer.Length);
                        memoryStream.Write(buffer, 0, count);
                    } while (count != 0);

                    result = memoryStream.ToArray();

                    using (BinaryWriter writer = new BinaryWriter(new FileStream(file, FileMode.Create)))
                        writer.Write(result);
                }
                return result;
            }
        }
    }
}
