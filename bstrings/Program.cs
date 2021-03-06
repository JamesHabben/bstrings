﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Fclp;
using Microsoft.Win32;
using NLog;
using NLog.Config;
using NLog.Targets;
using System.Data.SQLite;

namespace bstrings
{
	internal class Program
	{
		private static Logger _logger;
		private static Stopwatch _sw;
		private static readonly Dictionary<string, string> _regExPatterns = new Dictionary<string, string>();
		private static readonly Dictionary<string, string> _regExDesc = new Dictionary<string, string>();

		private static bool CheckForDotnet46()
		{
			using (
				var ndpKey =
					RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
						.OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\\"))
			{
				var releaseKey = Convert.ToInt32(ndpKey.GetValue("Release"));

				return (releaseKey >= 393295);
			}
		}

		private static void Main(string[] args)
		{
			SetupNLog();
			SetupPatterns();

			_logger = LogManager.GetCurrentClassLogger();


			if (!CheckForDotnet46())
			{
				_logger.Warn(".net 4.6 not detected. Please install .net 4.6 and try again.");
				return;
			}


			var p = new FluentCommandLineParser<ApplicationArguments>
			{
				IsCaseSensitive = false
			};

			p.Setup(arg => arg.File)
				.As('f')
				.WithDescription("File to search. This is required").Required();

			p.Setup(arg => arg.SaveTo)
				.As('o')
				.WithDescription("File to save results to");

            p.Setup(arg => arg.SqliteOutput)
                .As("sql")
                .WithDescription("Set a file path to output string into SQLite format");

			p.Setup(arg => arg.GetAscii)
				.As('a')
				.SetDefault(true)
				.WithDescription("If set, look for ASCII strings. Default is true. Use -a false to disable");

			p.Setup(arg => arg.GetUnicode)
				.As('u')
				.SetDefault(true)
				.WithDescription("If set, look for Unicode strings. Default is true. Use -u false to disable");

			p.Setup(arg => arg.MinimumLength)
				.As('m').SetDefault(3).WithDescription("Minimum string length. Default is 3");

			p.Setup(arg => arg.BlockSizeMB)
				.As('b').SetDefault(512).WithDescription("Chunk size in MB. Valid range is 1 to 1024. Default is 512");

			p.Setup(arg => arg.Quiet)
				.As('q').SetDefault(false).WithDescription("Quiet mode (Do not show header or total number of hits)");

			p.Setup(arg => arg.MaximumLength)
				.As('x').SetDefault(-1).WithDescription("Maximum string length. Default is unlimited");

			p.Setup(arg => arg.GetPatterns)
				.As('p').SetDefault(false).WithDescription("Display list of built in regular expressions");

			p.Setup(arg => arg.LookForString)
				.As("ls")
				.SetDefault(string.Empty)
				.WithDescription("String to look for. When set, only matching strings are returned.");

			p.Setup(arg => arg.LookForRegex)
				.As("lr")
				.SetDefault(string.Empty)
				.WithDescription("Regex to look for. When set, only matching strings are returned.");

			p.Setup(arg => arg.SortAlpha)
				.As("sa").SetDefault(false).WithDescription("Sort results alphabetically");

			p.Setup(arg => arg.SortLength)
				.As("sl").SetDefault(false).WithDescription("Sort results by length");

			//  p.Setup(arg => arg.ShowOffset).As('o').SetDefault(false).WithDescription("Show offset to hit before string");

			var header =
				$"bstrings version {Assembly.GetExecutingAssembly().GetName().Version}" +
				"\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
				"\r\nhttps://github.com/EricZimmerman/bstrings";

			var footer = @"Examples: bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls URL" + "\r\n\t " +
						 @" bstrings.exe -f ""C:\Temp\someFile.txt"" --lr guid" + "\r\n\t " +
						 @" bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc -sa" + "\r\n\t " +
						 @" bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc -sa -m 15 -x 22" + "\r\n\t " +
						 @" bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls mui -sl" + "\r\n\t ";

			p.SetupHelp("?", "help").WithHeader(header).Callback(text => _logger.Info(text + "\r\n" + footer));


			var result = p.Parse(args);

			if (result.HelpCalled)
			{
				return;
			}

			if (p.Object.GetPatterns)
			{
				_logger.Info("Name \t\tDescription");
				foreach (var regExPattern in _regExPatterns)
				{
					var desc = _regExDesc[regExPattern.Key];
					_logger.Info($"{regExPattern.Key}\t{desc}");
				}

				_logger.Info("");
				_logger.Info("To use a built in pattern, supply the Name to the --lr switch");

				return;
			}

			if (result.HasErrors)
			{
				_logger.Error("");
				_logger.Error(result.ErrorText);

				p.HelpOption.ShowHelp(p.Options);

				return;
			}

			if (!File.Exists(p.Object.File))
			{
				_logger.Warn($"'{p.Object.File}' not found. Exiting");
				return;
			}

			if (!p.Object.Quiet)
			{
				_logger.Info(header);
				_logger.Info("");
			}

			_sw = new Stopwatch();
			_sw.Start();

			var hits = new HashSet<string>();

			var regPattern = p.Object.LookForRegex;

			if (_regExPatterns.ContainsKey(p.Object.LookForRegex))
			{
				regPattern = _regExPatterns[p.Object.LookForRegex];
			}

			if (regPattern.Length > 0 && !p.Object.Quiet)
			{
				_logger.Info($"Searching via RegEx pattern: {regPattern}");
				_logger.Info("");
			}

			Regex reg = null;

			try
			{
				reg = new Regex(regPattern,
					RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
			}
			catch (Exception ex)
			{
				_logger.Error($"Error setting up regular expression: {ex.Message}");
				return;
			}


			if (p.Object.SaveTo.Length > 0)
			{
				var dir = Path.GetDirectoryName(p.Object.SaveTo);

				if (dir != null && Directory.Exists(dir) == false)
				{
					try
					{
						Directory.CreateDirectory(dir);
					}
					catch (Exception)
					{
						_logger.Warn($"Invalid path: '{p.Object.SaveTo}'. Results will not be saved to a file.");
						_logger.Info("");
						p.Object.SaveTo = string.Empty;
					}
				}
				else
				{
					if (dir == null)
					{
						_logger.Warn($"Invalid path: '{p.Object.SaveTo}");
						p.Object.SaveTo = string.Empty;
					}
				}

				if (p.Object.SaveTo.Length > 0 && !p.Object.Quiet)
				{
					_logger.Info($"Saving hits to '{p.Object.SaveTo}'");
					_logger.Info("");
				}
			}

			var minLength = 3;
			if (p.Object.MinimumLength > 0)
			{
				minLength = p.Object.MinimumLength;
			}

			var maxLength = -1;

			if (p.Object.MaximumLength > minLength)
			{
				maxLength = p.Object.MaximumLength;
			}

			var chunkSizeMb = p.Object.BlockSizeMB < 1 || p.Object.BlockSizeMB > 1024 ? 512 : p.Object.BlockSizeMB;
			var chunkSizeBytes = chunkSizeMb*1024*1024;

			var fileSizeBytes = new FileInfo(p.Object.File).Length;
			var bytesRemaining = fileSizeBytes;
			long offset = 0;

			var chunkIndex = 1;
			var totalChunks = (fileSizeBytes/chunkSizeBytes) + 1;
			var hsuffix = totalChunks == 1 ? "" : "s";

			if (!p.Object.Quiet)
			{
				_logger.Info($"Command line: {string.Join(" ", args)}");
				_logger.Info("");
				_logger.Info(
					$"Searching {totalChunks:N0} chunk{hsuffix} ({chunkSizeMb} MB each) across {GetSizeReadable(fileSizeBytes)}");
				_logger.Info("");
			}

			try
			{
				using (var mmf = MemoryMappedFile.CreateFromFile(p.Object.File, FileMode.Open, "source"))
				{
					while (bytesRemaining > 0)
					{
						if (bytesRemaining <= chunkSizeBytes)
						{
							chunkSizeBytes = (int) bytesRemaining;
						}

						using (var accessor = mmf.CreateViewStream(offset, chunkSizeBytes, MemoryMappedFileAccess.Read))
						{
							var chunk = new byte[chunkSizeBytes];

							accessor.Read(chunk, 0, chunkSizeBytes);

							offset += chunkSizeBytes;
							bytesRemaining -= chunkSizeBytes;

							if (p.Object.GetUnicode)
							{
								var uh = GetUnicodeHits(chunk, minLength, maxLength);
								foreach (var h in uh)
								{
									hits.Add(h);
								}
							}

							if (p.Object.GetAscii)
							{
								var ah = GetAsciiHits(chunk, minLength, maxLength);
								foreach (var h in ah)
								{
									hits.Add(h);
								}
							}

							if (!p.Object.Quiet)
							{
								_logger.Info(
									$"Chunk {chunkIndex:N0} of {totalChunks:N0} finished. Total strings so far: {hits.Count:N0} Elapsed time: {_sw.Elapsed.TotalSeconds:N3} seconds. Average strings/sec: {(hits.Count/_sw.Elapsed.TotalSeconds):N0}");
							}
						}
						chunkIndex += 1;
					}
				}
			}
			catch (Exception ex)
			{
				_logger.Info("");
				_logger.Error($"Error: {ex.Message}");
			}
			_sw.Stop();

			if (!p.Object.Quiet)
			{
				_logger.Info("");
			}

			if (p.Object.SortAlpha)
			{
				var tempList = hits.ToList();
				tempList.Sort();
				hits = new HashSet<string>(tempList);
			}
			else if (p.Object.SortLength)
			{
				var tempList = SortByLength(hits.ToList()).ToList();
				hits = new HashSet<string>(tempList);
			}

			var counter = 0;


			//set up highlighting
			var words = new HashSet<string>();
			if (p.Object.LookForString.Length > 0)
			{
				words.Add(p.Object.LookForString);
			}
			else if (p.Object.LookForRegex.Length > 0)
			{
				words.Add(regPattern);
			}

			AddHighlightingRules(words.ToList(), regPattern.Length > 0);

			StreamWriter sw = null;
			if (p.Object.SaveTo.Length > 0)
			{
				sw = new StreamWriter(p.Object.SaveTo, false);
			}

            SQLiteConnection dbCon = new SQLiteConnection();
            SQLiteCommand dbCmd = new SQLiteCommand();
            if (p.Object.SqliteOutput != string.Empty)
            {
                SQLiteConnection.CreateFile(p.Object.SqliteOutput);
                //SQLiteConnection dbCon;
                dbCon = new SQLiteConnection("Data Source='" + p.Object.SqliteOutput + "';Version=3;");
                dbCon.Open();
                string createTable = "create table strings (string TEXT, length INTEGER, ascii_only INTEGER default 0 ";
                /*string createCounts = "CREATE VIEW [counts] AS select " +
                                        "(select count(*) from strings) as total," +
                                        "(select count(*) from strings where ascii_only = 1) as ascii_only," +
                                        "(select count(*) from strings where ascii_only = 0) as non_ascii";*/
                string createCounts = "CREATE VIEW [counts] AS select '1_total' as name, (select count(*) from strings) as total " +
                                        "union select '2_ascii_only', (select count(*) from strings where ascii_only = 1) " +
                                        "union select '3_non_ascii', (select count(*) from strings where ascii_only = 0) ";
                string createOther = "create view [view_other] as select * from strings where string not in (";
                foreach (string key in _regExPatterns.Keys)
                {
                    createTable += ", " + key + " INTEGER default 0 ";
                    //createCounts += ",(select count(*) from strings where " + key + " = 1) as " + key;
                    createCounts += "union select '" + key + "', (select count(*) from strings where " + key + " = 1) ";
                    createOther += "select string from view_" + key + " union ";
                }
                createTable += ")";
                createOther = createOther.Remove(createOther.Length - 6) + ")";
                //_logger.Info(createOther);
                dbCmd = new SQLiteCommand(createTable, dbCon);
                dbCmd.ExecuteNonQuery();
                dbCmd.CommandText = "create index string_idx on strings (string)";
                dbCmd.ExecuteNonQuery();
                dbCmd.CommandText = createCounts;
                dbCmd.ExecuteNonQuery();
                dbCmd.CommandText = "create view [view_ascii] as select * from strings where ascii_only = 1";
                dbCmd.ExecuteNonQuery();
                dbCmd.CommandText = "create view [view_non_ascii] as select * from strings where ascii_only = 0";
                dbCmd.ExecuteNonQuery();
                foreach (string key in _regExPatterns.Keys)
                {
                    dbCmd.CommandText = "create view [view_" + key + "] as select * from strings where " + key + " = 1";
                    dbCmd.ExecuteNonQuery();
                }
                dbCmd.CommandText = createOther;
                dbCmd.ExecuteNonQuery();
                dbCmd.CommandText = "begin";
                dbCmd.ExecuteNonQuery();
                _logger.Info("Writing hits to SQLite... (Cancel during this will lose all strings)");
            }


			foreach (var hit in hits)
			{
				if (hit.Length == 0)
				{
					continue;
				}

				if (p.Object.LookForString.Length > 0 || p.Object.LookForRegex.Length > 0)
				{
					if (p.Object.LookForString.Length > 0 &&
						hit.IndexOf(p.Object.LookForString, StringComparison.InvariantCultureIgnoreCase) >= 0)
					{
						counter += 1;
                        if (p.Object.SqliteOutput != string.Empty)
                        {
                            DbInsertString(hit, dbCon);
                        }
                        else
                        {
                            _logger.Info(hit);
                            sw?.WriteLine(hit);
                        }
                    }
					else if (p.Object.LookForRegex.Length > 0)
					{
						if (!reg.IsMatch(hit))
						{
							continue;
						}
						counter += 1;
                        if (p.Object.SqliteOutput != string.Empty)
                        {
                            DbInsertString(hit, dbCon);
                        }
                        else
                        {
                            _logger.Info(hit);
                            sw?.WriteLine(hit);
                        }
                    }
                }
				else
				{
					counter += 1;
                    if (p.Object.SqliteOutput != string.Empty)
                    {
                        DbInsertString(hit, dbCon);
                    }
                    else
                    {
                        _logger.Info(hit);
                        sw?.WriteLine(hit);
                    }
                }

                if (p.Object.SqliteOutput != string.Empty)
                {
                    DrawProgressBar(counter, hits.Count, Console.WindowWidth -10, '█');
                }
            }
            Console.CursorVisible = true;

            if (p.Object.SqliteOutput != string.Empty)
            {
                dbCmd.CommandText = "commit";
                dbCmd.ExecuteNonQuery();
            }

			if (sw != null)
			{
				sw.Flush();
				sw.Close();
			}

			if (!p.Object.Quiet)
			{
				var suffix = counter == 1 ? "" : "s";

				_logger.Info("");
				_logger.Info(
					$"Found {counter:N0} string{suffix} in {_sw.Elapsed.TotalSeconds:N3} seconds. Average strings/sec: {(hits.Count/_sw.Elapsed.TotalSeconds):N0}");
			}


        }

        private static void DbInsertString (string hit, SQLiteConnection dbCon)
        {
            string insertText1 = "INSERT INTO strings (string, length, ascii_only";
            string insertText2 = ") VALUES (?," + hit.Length + "," + (IsAscii(hit)?1:0);
            foreach (KeyValuePair<string, string> re in _regExPatterns)
            {
                Regex reg = new Regex(re.Value, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
                if (reg.IsMatch(hit))
                {
                    insertText1 += "," + re.Key;
                    insertText2 += ",1";
                }

            }
            insertText2 += ")";
            //_logger.Info(insertText1 + insertText2);
            SQLiteCommand dbCmd = new SQLiteCommand(insertText1 + insertText2, dbCon);
            SQLiteParameter data = new SQLiteParameter();
            dbCmd.Parameters.Add(data);
            data.Value = hit;
            dbCmd.ExecuteNonQuery();
        }

        private static bool IsAscii(string value)
        {
            // ASCII encoding replaces non-ascii with question marks, so we use UTF8 to see if multi-byte sequences are there
            return Encoding.UTF8.GetByteCount(value) == value.Length;
        }

        private static void DrawProgressBar(int complete, int maxVal, int barSize, char progressCharacter)
        {
            Console.CursorVisible = false;
            int left = Console.CursorLeft;
            decimal perc = (decimal)complete / (decimal)maxVal;
            int chars = (int)Math.Floor(perc / ((decimal)1 / (decimal)barSize));
            string p1 = String.Empty, p2 = String.Empty;

            for (int i = 0; i < chars; i++) p1 += progressCharacter;
            for (int i = 0; i < barSize - chars; i++) p2 += progressCharacter;

            Console.Write('\r');
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(p1);
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.Write(p2);

            Console.ResetColor();
            Console.Write(" {0}%", (perc * 100).ToString("N2"));
            Console.CursorLeft = left;
        }

        private static string GetSizeReadable(long i)
		{
			var sign = (i < 0 ? "-" : "");
			double readable = (i < 0 ? -i : i);
			string suffix;
			if (i >= 0x1000000000000000) // Exabyte
			{
				suffix = "EB";
				readable = i >> 50;
			}
			else if (i >= 0x4000000000000) // Petabyte
			{
				suffix = "PB";
				readable = i >> 40;
			}
			else if (i >= 0x10000000000) // Terabyte
			{
				suffix = "TB";
				readable = i >> 30;
			}
			else if (i >= 0x40000000) // Gigabyte
			{
				suffix = "GB";
				readable = i >> 20;
			}
			else if (i >= 0x100000) // Megabyte
			{
				suffix = "MB";
				readable = i >> 10;
			}
			else if (i >= 0x400) // Kilobyte
			{
				suffix = "KB";
				readable = i;
			}
			else
			{
				return i.ToString(sign + "0 B"); // Byte
			}
			readable = readable/1024;

			return sign + readable.ToString("0.### ") + suffix;
		}

		private static void SetupPatterns()
		{
			_regExDesc.Add("guid", "\tFinds GUIDs");
			_regExDesc.Add("usPhone", "\tFinds US phone numbers");
			_regExDesc.Add("unc", "\tFinds UNC paths");
			_regExDesc.Add("mac", "\tFinds MAC addresses");
			_regExDesc.Add("ssn", "\tFinds US Social Security Numbers");
			_regExDesc.Add("cc", "\tFinds credit card numbers");

			_regExDesc.Add("ipv4", "\tFinds IP version 4 addresses");
			_regExDesc.Add("ipv6", "\tFinds IP version 6 addresses");
			_regExDesc.Add("email", "\tFinds email addresses");
			_regExDesc.Add("zip", "\tFinds zip codes");
			_regExDesc.Add("urlUser", "\tFinds usernames in URLs");
			_regExDesc.Add("url3986", "\tFinds URLs according to RFC 3986");
			_regExDesc.Add("xml", "\tFinds XML/HTML tags");
            _regExDesc.Add("sid", "\tFinds Microsoft Security Identifiers (SID)");
            _regExDesc.Add("win_path", "\tFinds Windows style paths (C:\folder1\folder2\file.txt)");
            _regExDesc.Add("var_set", "\tFinds environment variables being set (OS=Windows_NT)");
            _regExDesc.Add("reg_path", "\tFinds paths with registry hives");
            _regExDesc.Add("b64", "\tFinds valid formatted base 64 strings");

            _regExPatterns.Add("b64", @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$");
            _regExPatterns.Add("reg_path", @"([a-z0-9]\\)*(software\\)|(sam\\)|(system\\)|(security\\)[a-z0-9\\]+");
            _regExPatterns.Add("var_set", @"^[a-z_0-9]+=[\\/:\*\?<>|;\- _a-z0-9]+");
            _regExPatterns.Add("win_path",
                @"(?:""?[a-zA-Z]\:|\\\\[^\\\/\:\*\?\<\>\|]+\\[^\\\/\:\*\?\<\>\|]*)\\(?:[^\\\/\:\*\?\<\>\|]+\\)*\w([^\\\/\:\*\?\<\>\|])*");
            _regExPatterns.Add("sid", @"^S-\d-\d+-(\d+-){1,14}\d+$");
            _regExPatterns.Add("xml", @"\A<([A-Z][A-Z0-9]*)\b[^>]*>(.*?)</\1>\z");
			_regExPatterns.Add("guid", @"\b[A-F0-9]{8}(?:-[A-F0-9]{4}){3}-[A-F0-9]{12}\b");
			_regExPatterns.Add("usPhone", @"\(?\b[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}\b");
			_regExPatterns.Add("unc", @"^\\\\(?<server>[a-z0-9 %._-]+)\\(?<share>[a-z0-9 $%._-]+)");
			_regExPatterns.Add("mac", "\\b[0-9A-F]{2}([-:]?)(?:[0-9A-F]{2}\\1){4}[0-9A-F]{2}\\b");
			_regExPatterns.Add("ssn", "\\b(?!000)(?!666)[0-8][0-9]{2}[- ](?!00)[0-9]{2}[- ](?!0000)[0-9]{4}\\b");
			_regExPatterns.Add("cc",
				"^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|6(?:011|5[0-9][0-9])[0-9]{12}|3[47][0-9]{13}|3(?:0[0-5]|[68][0-9])[0-9]{11}|(?:2131|1800|35\\d{3})\\d{11})$");
			_regExPatterns.Add("ipv4",
				@"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b");
			_regExPatterns.Add("ipv6", @"(?<![:.\w])(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}(?![:.\w])");
			_regExPatterns.Add("email", @"\A\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b\z");
			_regExPatterns.Add("zip", @"\A\b[0-9]{5}(?:-[0-9]{4})?\b\z");
			_regExPatterns.Add("urlUser", @"^[a-z0-9+\-.]+://(?<user>[a-z0-9\-._~%!$&'()*+,;=]+)@");
			_regExPatterns.Add("url3986", @"^
		[a-z][a-z0-9+\-.]*://                       # Scheme
		([a-z0-9\-._~%!$&'()*+,;=]+@)?              # User
		(?<host>[a-z0-9\-._~%]+                     # Named host
		|\[[a-f0-9:.]+\]                            # IPv6 host
		|\[v[a-f0-9][a-z0-9\-._~%!$&'()*+,;=:]+\])  # IPvFuture host
		(:[0-9]+)?                                  # Port
		(/[a-z0-9\-._~%!$&'()*+,;=:@]+)*/?          # Path
		(\?[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Query
		(\#[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Fragment
		$");
		}

		private static void AddHighlightingRules(List<string> words, bool isRegEx = false)
		{
			var target = (ColoredConsoleTarget) LogManager.Configuration.FindTargetByName("console");
			var rule = target.WordHighlightingRules.FirstOrDefault();

			var bgColor = ConsoleOutputColor.Green;
			var fgColor = ConsoleOutputColor.Red;

			if (rule != null)
			{
				bgColor = rule.BackgroundColor;
				fgColor = rule.ForegroundColor;
			}

			target.WordHighlightingRules.Clear();

			foreach (var word in words)
			{
				var r = new ConsoleWordHighlightingRule();
				r.IgnoreCase = true;
				if (isRegEx)
				{
					r.Regex = word;
				}
				else
				{
					r.Text = word;
				}
				r.ForegroundColor = fgColor;
				r.BackgroundColor = bgColor;

				r.WholeWords = false;
				target.WordHighlightingRules.Add(r);
			}
		}

		private static IEnumerable<string> SortByLength(IEnumerable<string> e)
		{
			var sorted = from s in e
				orderby s.Length ascending
				select s;
			return sorted;
		}

		private static List<string> GetUnicodeHits(byte[] bytes, int minSize, int maxSize)
		{
			var maxString = maxSize == -1 ? "" : maxSize.ToString();
			var mi2 = string.Format("{0}{1}{2}{3}{4}", "{", minSize, ",", maxString, "}");

			const string uniRange = "[\u0020-\u007E]";
			var regUni = new Regex($"{uniRange}{mi2}");
			var uniString = Encoding.Unicode.GetString(bytes);

			return (from Match match in regUni.Matches(uniString) select match.Value.Trim()).ToList();
		}

		private static List<string> GetAsciiHits(byte[] bytes, int minSize, int maxSize)
		{
			var maxString = maxSize == -1 ? "" : maxSize.ToString();
			var mi2 = string.Format("{0}{1}{2}{3}{4}", "{", minSize, ",", maxString, "}");

			const string ascRange = "[\x20-\x7E]";
			var regUni = new Regex($"{ascRange}{mi2}");
			var uniString = Encoding.UTF8.GetString(bytes);

			return (from Match match in regUni.Matches(uniString) select match.Value.Trim()).ToList();
		}

		private static void SetupNLog()
		{
			var config = new LoggingConfiguration();
			var loglevel = LogLevel.Info;

			var layout = @"${message}";

			var consoleTarget = new ColoredConsoleTarget();

			config.AddTarget("console", consoleTarget);

			consoleTarget.Layout = layout;

			var rule1 = new LoggingRule("*", loglevel, consoleTarget);
			config.LoggingRules.Add(rule1);

			LogManager.Configuration = config;
		}
	}

	internal class ApplicationArguments
	{
		public string File { get; set; }
		public string SaveTo { get; set; } = string.Empty;
		public bool GetAscii { get; set; } = true;
		public bool GetUnicode { get; set; } = true;
		public string LookForString { get; set; } = string.Empty;
		public string LookForRegex { get; set; } = string.Empty;
		public int MinimumLength { get; set; } = 3;
		public int MaximumLength { get; set; } = -1;
		public int BlockSizeMB { get; set; } = 512;
		public bool ShowOffset { get; set; } = false;
		public bool SortLength { get; set; } = false;
		public bool SortAlpha { get; set; } = false;
		public bool Quiet { get; set; } = false;
		public bool GetPatterns { get; set; } = false;

        public string SqliteOutput { get; set; } = string.Empty;
	}
}