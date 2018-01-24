using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Tenki {
	public static class Program {
		static void Main(string[] args) {

			if(args.Length == 0) {
				Console.WriteLine("Usage: TenkiConv.exe file1 file2 ...");
				Console.ReadLine();
				return;
			}

			foreach(string path in args) {
				if(path == null) continue;

				string filename = Path.GetFileName(path);
				string txtFile = Path.ChangeExtension(filename, "txt");
				string csvFile = Path.ChangeExtension(filename, "csv");
				string metaFile = Path.ChangeExtension(filename, "meta");

				string purename = Path.GetFileNameWithoutExtension(filename);
				string extension = Path.GetExtension(path).ToLower();

				try {
					Script script;
					switch(extension) {
						case ".csv":
							Console.Write($"Converting csv -> txt ({purename}): ");
							script = Script.ReadFromCsv(csvFile, metaFile);
							script.WriteToTxt(txtFile);
							Success("Done");
							break;
						case ".txt":
							Console.Write($"Converting txt -> csv ({purename}): ");
							script = Script.ReadFromTxt(txtFile);
							script.WriteToCsv(csvFile, metaFile);
							Success("Done");
							break;
						default:
							if(extension != ".meta") {
								Console.WriteLine("Skipping ungecognized file: " + filename);
							}
							break;
					}
				}
				catch(Exception e) {
					Error("Error");
					Error(e.Message);
				}
			}

			Console.WriteLine("Finished operation, press enter to close...");
			Console.ReadLine();
		}


		public class Script {
			public List<string> Commands = new List<string>();
			public List<TextLine> Lines = new List<TextLine>();
			public List<string> Names = new List<string>();

			public static Script ReadFromTxt(string filename) {
				var script = new Script();
				var lines = File.ReadAllLines(filename, Encoding.GetEncoding("Shift-JIS"));

				LineType last = LineType.Other;
				string speaker = null;

				foreach(string line in lines) {
					LineType type = Type(line);

					switch(type) {
						case LineType.Text:
							if(last == LineType.Text) {
								int id = script.Lines.Count - 1;
								script.Lines[id].Append(line);
							}
							else {
								int id = script.Lines.Count;
								script.Lines.Add(new TextLine(speaker, line));
								script.Commands.Add($"@L{id}");
							}

							break;
						case LineType.Name: {
								int id = script.Names.IndexOf(line);
								if(id < 0) {
									id = script.Names.Count;
									script.Names.Add(line);
								}
								script.Commands.Add($"@N{id}");
								speaker = line;
							}
							break;
						default:
							script.Commands.Add(line);
							speaker = null;
							break;
					}

					last = type;
				}

				return script;
			}

			public static Script ReadFromCsv(string csvFilename, string metaFilename) {
				if(!File.Exists(metaFilename)) {
					throw new Exception("Missing .meta file");
				}

				var script = new Script {
					Commands = File.ReadAllLines(metaFilename).ToList()
				};

				var csvReader = new StreamReader(new FileStream(csvFilename, FileMode.Open));

				List<string> entry;
				while((entry = ReadCsvLine(csvReader)) != null) {
					if(entry.Count == 0 || entry[0] == null) continue;

					if(entry[0].StartsWith("@L")) {
						int id = int.Parse(entry[0].Substring(2));
						while(script.Lines.Count <= id) script.Lines.Add(null);
						script.Lines[id] = new TextLine(
							entry.Count > 1 && !string.IsNullOrWhiteSpace(entry[1])
								? entry[1]
								: null,
							entry.Count > 2
								? (entry.Count > 3 && !string.IsNullOrWhiteSpace(entry[3])
									? entry[3]
									: entry[2])
								: "");
					}
					else if(entry[0].StartsWith("@N")) {
						int id = int.Parse(entry[0].Substring(2));
						while(script.Names.Count <= id) script.Names.Add(null);
						script.Names[id] =
							entry.Count > 2
								? (entry.Count > 3 && !string.IsNullOrWhiteSpace(entry[3])
									? entry[3]
									: entry[2])
								: "";
					}
				}

				csvReader.Close();
				return script;
			}

			public void WriteToTxt(string filename) {
				var lines = new List<string>();

				foreach(string command in Commands) {
					if(command.StartsWith("@L")) {
						int id = int.Parse(command.Substring(2));
						lines.Add(Lines[id].Text);
					}
					else if(command.StartsWith("@N")) {
						int id = int.Parse(command.Substring(2));
						lines.Add(Names[id]);
					}
					else {
						lines.Add(command);
					}
				}

				File.WriteAllLines(filename, lines, Encoding.GetEncoding("Shift-JIS"));
			}

			public void WriteToCsv(string csvFilename, string metaFilename) {
				const char s = ',';

				var lines = new List<string>();
				lines.Add($"ID{s}Speaker{s}Original{s}Translation");

				lines.Add($"{s}{s}{s}");
				lines.Add($"Names{s}{s}{s}");
				lines.AddRange(Names.Select((n, i) => $"@N{i}{s}{s}\"{n.Replace("\"", "\"\"")}\"{s}"));

				lines.Add($"{s}{s}{s}");
				lines.Add($"Lines{s}{s}{s}");
				lines.AddRange(Lines.Select((l, i) => $"@L{i}{s}\"{l.Speaker?.Replace("\"", "\"\"")}\"{s}\"{l.Text.Replace("\"", "\"\"")}\"{s}"));

				File.WriteAllLines(csvFilename, lines);
				File.WriteAllLines(metaFilename, Commands);
			}

			public static List<string> ReadCsvLine(TextReader reader) {

				if(reader.Peek() < 0) return null;

				var list = new List<string> {
					ReadCsvValue(reader)
				};

				string value;
				while(reader.Read() == ',') {
					list.Add(ReadCsvValue(reader));
				}

				return list;
			}

			public static string ReadCsvValue(TextReader reader) {
				var sb = new StringBuilder();
				int ch;

				if(reader.Peek() < 0) return null;

				if(reader.Peek() == '"') {
					// skip quote
					reader.Read();
					while(reader.Peek() >= 0 && !IsQuoteEnd(reader))
						sb.Append((char)reader.Read());
				}
				else {
					while(reader.Peek() >= 0 && reader.Peek() != ',' && !IsLineEnd(reader))
						sb.Append((char)reader.Read());
				}

				return sb.ToString();
			}

			public static bool IsQuoteEnd(TextReader reader) {
				int ch = reader.Peek();
				if(ch < 0) return true;
				if(ch != '"') return false;
				reader.Read();
				return reader.Peek() != '"';
			}

			public static bool IsLineEnd(TextReader reader) {
				int ch = reader.Peek();
				if(ch < 0) return true;
				if(ch == '\n') {
					reader.Read();
					return true;
				}
				if(ch != '\r') return false;

				reader.Read();
				if(ch == '\n') {
					reader.Read();
				}
				return true;
			}

			public static Regex NameRegex = new Regex("\uff08([\uff10-\uff19]{4})\uff09"); // （[０-９]{4}）
			public static LineType Type(string line) =>
				line.Contains("//") || line.StartsWith("EF_") || line.StartsWith("FC_")
					? LineType.Other
					: string.IsNullOrWhiteSpace(line)
						? LineType.Linebreak
						: NameRegex.IsMatch(line)
							? LineType.Name
							: LineType.Text;

			public class TextLine {
				public string Speaker;
				public string Text;

				public TextLine(string speaker, string text) {
					Speaker = speaker;
					Text = text;
				}

				public void Append(string line) {
					Text += Environment.NewLine + line;
				}

				public override string ToString() => (Speaker != null ? Speaker + ": \"" + Text + "\"" : Text);
			}

			public enum LineType {
				Text, Name, Linebreak, Other
			}
		}

		public static void Success(string message) {
			var color = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine(message);
			Console.ForegroundColor = color;
		}

		public static void Error(string message) {
			var color = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);
			Console.ForegroundColor = color;
		}
	}
}
