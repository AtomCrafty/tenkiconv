using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;

namespace Tenki;

public static class Program {
	static void Main(string[] args) {
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

		/*
		const string DataPath = @"D:\Projects\tenkiconv\TestData";
		var sptFiles = Directory.GetFiles(DataPath, "*.spt");
		var entryTypes = sptFiles
			.SelectMany(file => File.ReadAllBytes(file)
				.Skip(4)
				.Chunk(0x20)
				.Select(entry => BitConverter.ToInt32(entry, 0)))
			.Distinct()
			.ToArray();

		Console.WriteLine(string.Join(", ", entryTypes.Select(type => type.ToString("X2"))));
		return;
		*/

		if(args.Length == 0) {
			Console.WriteLine("Usage: TenkiConv.exe file1 file2 ...");
			Console.ReadLine();
			return;
		}

		foreach(string path in args) {
			ConvertFile(path);
		}

		Console.WriteLine("Finished operation, press enter to close...");
		Console.ReadLine();
	}

	private static void ConvertFile(string path) {
		string fileName = Path.GetFileName(path);
		string txtFile = Path.ChangeExtension(path, "txt");
		string csvFile = Path.ChangeExtension(path, "csv");
		string metaFile = Path.ChangeExtension(path, "meta");

		string pureName = Path.GetFileNameWithoutExtension(fileName);
		string extension = Path.GetExtension(path).ToLower();

		try {
			Script script;
			switch(extension) {
				case ".csv":
				case ".meta":
					Console.Write($"Converting csv -> txt ({pureName}): ");
					script = Script.LoadWithTranslations(csvFile, metaFile);
					script.Validate();
					script.Internalize();
					script.Validate();
					script.WriteToTxt(txtFile);
					Success("Done");
					break;

				case ".txt":
					Console.Write($"Converting txt -> csv ({pureName}): ");
					script = Script.ParseCode(txtFile);
					script.Validate();
					script.Externalize();
					script.Validate();
					script.WriteToCsv(csvFile, metaFile);
					Success("Done");
					break;

				default:
					Console.WriteLine("Skipping unrecognized file: " + fileName);
					break;
			}
		}
		finally {
		}
		//catch(Exception e) {
		//	Error("Error");
		//	Error(e.Message);
		//}
	}

	public class Script {
		public readonly List<ScriptLine> Lines;
		public readonly List<ScriptCommand> Commands;
		public readonly List<ScriptSection> Sections;

		public Dictionary<int, string>? Translations;
		public Dictionary<int, string>? Speakers;
		public Dictionary<int, string>? Names;

		public Script(List<ScriptLine> lines, List<ScriptCommand> commands, List<ScriptSection> sections) {
			Lines = lines;
			Commands = commands;
			Sections = sections;
		}

		public static readonly Regex SectionPattern = new(@"^\*\*\*S[SC]_(?<sectionName>\w\d+_\d+)_");
		public static readonly Regex NamePattern = new(@"^(?<name>.*?)\s*\uff08(?<id>[\uff10-\uff19]{4})\uff09"); // （[０-９]{4}）

		public static Script ParseCode(string fileName) {
			var input = File.ReadAllLines(fileName, Encoding.GetEncoding(932));

			ScriptLine? lastLine = null;
			LineType lastLineType = LineType.None;
			ScriptSection? currentSection = null;
			int commandIndex = 0;

			var lines = new List<ScriptLine>();
			var commands = new List<ScriptCommand>();
			var sections = new List<ScriptSection>();

			ScriptLine ParseLine(string lineText, int lineNo) {
				var type = GetLineType(lineText);
				switch(type) {
					case LineType.Text:
					case LineType.TextContinuation: {
						if(lastLineType is LineType.Text or LineType.TextContinuation) {
							var textCommand = lines.Last(line => line.Type == LineType.Text).Command as TextCommand;
							Debug.Assert(textCommand != null);
							var line = new ScriptLine(LineType.TextContinuation, lineText, lineNo);
							textCommand.AddContinuation(line);
							return line;
						}

						Debug.Assert(currentSection != null, "Command before section start");
						var command = new TextCommand(currentSection, commandIndex++);

						if(lastLineType == LineType.Speaker) {
							command.NameLine = lastLine;
						}

						return new ScriptLine(LineType.Text, lineText, lineNo, command);
					}

					case LineType.Command: {
						Debug.Assert(currentSection != null, "Command before section start");
						var command = new ScriptCommand(currentSection, commandIndex++);

						return new ScriptLine(LineType.Command, lineText, lineNo, command);
					}

					case LineType.Section: {
						var match = SectionPattern.Match(lineText);
						Debug.Assert(match.Success);
						currentSection = new ScriptSection(Path.Combine(Path.GetDirectoryName(fileName)!, match.Groups["sectionName"].Value + ".spt"));
						sections.Add(currentSection);
						commandIndex = 0;
						return new ScriptLine(LineType.Section, lineText, lineNo);
					}

					case LineType.None:
					case LineType.Speaker:
					default:
						return new ScriptLine(type, lineText, lineNo);
				}
			}

			for(var lineNo = 0; lineNo < input.Length; lineNo++) {
				var lineText = input[lineNo];
				var line = ParseLine(lineText, lineNo);
				lines.Add(line);
				lastLine = line;
				lastLineType = line.Type;
				if(line.Command != null) commands.Add(line.Command);
			}

			return new Script(lines, commands, sections);
		}

		public void Externalize() {
			if(Translations != null || Speakers != null || Names != null) return;

			Translations = new Dictionary<int, string>();
			Speakers = new Dictionary<int, string>();
			Names = new Dictionary<int, string>();

			int lineCount = 1;
			int nameCount = 1;
			var names = new Dictionary<string, int>();

			foreach(var command in Commands.OfType<TextCommand>()) {
				var text = command.SourceLine.Text;
				command.SourceLine.Text = "@L" + lineCount;
				Debug.Assert(!text.StartsWith('@'));

				foreach(var continuationLine in command.ContinuationLines) {
					text += '\n' + continuationLine.Text;
					continuationLine.Text = "@--";
				}

				Translations[lineCount] = text;

				if(command.NameLine != null) {
					var match = NamePattern.Match(command.NameLine.Text);
					Debug.Assert(match.Success);
					string name = match.Groups["name"].Value;
					Debug.Assert(!name.StartsWith('@'));
					string key = "@N" + nameCount;

					if(names.TryGetValue(name, out var nameId)) {
						key = "@N" + nameId;
					}
					else {
						names.Add(name, nameCount);
						Names[nameCount] = name;
						nameCount++;
					}

					Speakers[lineCount] = name;
					command.NameLine.Text = key + command.NameLine.Text[name.Length..];
				}

				lineCount++;
			}
		}

		public void Internalize() {
			if(Translations == null || Speakers == null || Names == null) return;

			foreach(var command in Commands.OfType<TextCommand>()) {
				var key = command.SourceLine.Text;
				Debug.Assert(key.StartsWith("@L"));

				foreach(var continuationLine in command.ContinuationLines!) {
					Debug.Assert(continuationLine.Text == "@--");
				}

				var translation = Translations[int.Parse(key[2..])].Replace("\\n", "\n").Split('\n');
				ChangeText(command, translation);

				if(command.NameLine != null) {
					var match = NamePattern.Match(command.NameLine.Text);
					Debug.Assert(match.Success);
					string nameKey = match.Groups["name"].Value;
					Debug.Assert(nameKey.StartsWith("@N"));
					string name = Names[int.Parse(nameKey[2..])];
					command.NameLine.Text = name + command.NameLine.Text[nameKey.Length..];
				}
			}

			UpdateLineNumbers();

			Translations = null;
			Speakers = null;
			Names = null;
		}

		private void ChangeText(TextCommand command, string[] text) {
			var lineIndex = Lines.IndexOf(command.SourceLine);
			for(var i = 0; i < command.ContinuationLines.Count; i++) {
				Debug.Assert(command.ContinuationLines[i] == Lines[lineIndex + i + 1]);
			}

			int oldLineCount = command.ContinuationLines.Count + 1;
			int newLineCount = text.Length;

			Debug.Assert(newLineCount > 0);

			if(oldLineCount > newLineCount) {
				int linesToRemove = oldLineCount - newLineCount;

				// remove lines
				Lines.RemoveRange(lineIndex + newLineCount, linesToRemove);
				command.ContinuationLines.RemoveRange(newLineCount - 1, linesToRemove);

				// update line numbers
				for(int i = lineIndex + newLineCount; i < Lines.Count; i++) {
					Lines[i].LineNumber -= linesToRemove;
				}
			}
			else if(oldLineCount < newLineCount) {
				int linesToAdd = newLineCount - oldLineCount;

				// add lines
				var newLines = new ScriptLine[linesToAdd];
				for(int i = 0; i < linesToAdd; i++) {
					newLines[i] = new ScriptLine(LineType.TextContinuation, "@--", lineIndex + oldLineCount + i);
				}
				Lines.InsertRange(lineIndex + oldLineCount, newLines);
				command.ContinuationLines.AddRange(newLines);

				// update line numbers
				for(int i = lineIndex + newLineCount; i < Lines.Count; i++) {
					Lines[i].LineNumber += linesToAdd;
				}
			}

			command.SourceLine.Text = text[0];
			for(int i = 1; i < newLineCount; i++) {
				command.ContinuationLines[i - 1].Text = text[i];
			}

			// only true the first time we convert back
			//Debug.Assert(command.SptEntry.Field14 == oldLineCount);

			// only set the count here, line numbers are updated later
			command.SptEntry.Field14 = command.NameLine != null ? newLineCount + 1 : newLineCount;
		}

		private void UpdateLineNumbers() {
			foreach(var command in Commands.OfType<TextCommand>()) {
				if(command.NameLine != null) {
					command.SptEntry.Field10 = command.NameLine.LineNumber;
				}
				else {
					command.SptEntry.Field10 = command.SourceLine.LineNumber;
				}
			}
		}

		public void Validate() {
			for(int i = 0; i < Lines.Count; i++) {
				var line = Lines[i];
				Debug.Assert(line.Command == null || line.Command.SourceLine == line);

				switch(line.Type) {
					case LineType.Speaker:
						// every speaker line must be followed by a text line
						Debug.Assert(Lines[i + 1].Type == LineType.Text);
						break;

					case LineType.TextContinuation:
						// every text continuation line must follow a text or text continuation line
						int j = i - 1;
						while(Lines[j].Type == LineType.TextContinuation) j--;
						var textLine = Lines[j];
						Debug.Assert(textLine.Type == LineType.Text);
						Debug.Assert(textLine.Command is TextCommand textCommand && textCommand.ContinuationLines[i - j - 1] == line);
						break;
				}
			}

			foreach(var section in Sections) {
				if(section.Commands.Count != section.Entries.Length) {
					for(int i = 0; i < Math.Min(section.Commands.Count, section.Entries.Length); i++) {
						var command = section.Commands[i];
						var entry = section.Entries[i];
						Console.WriteLine($"{entry.Type,11} {command.SourceLine}");
						Debug.Assert((command is TextCommand) == (entry.Type == SptEntryType.Text));
					}
				}

				Debug.Assert(section.Commands.Count == section.Entries.Length);
			}

			foreach(var command in Commands) {
				Debug.Assert(command.Section.Commands[command.CommandIndex] == command);
				Debug.Assert(command.SourceLine.Command == command);
			}
		}

		public static Script LoadWithTranslations(string csvFileName, string metaFileName) {
			if(!File.Exists(metaFileName)) {
				throw new Exception("Missing .meta file");
			}

			var script = ParseCode(metaFileName);

			var translation = new Dictionary<int, string>();
			var speakers = new Dictionary<int, string>();
			var names = new Dictionary<int, string>();

			using var csvReader = new CsvReader(File.OpenText(csvFileName), CultureInfo.InvariantCulture);

			csvReader.Read();
			csvReader.ReadHeader();

			while(csvReader.Read()) {
				var key = csvReader.GetField("ID") ?? throw new Exception("Missing id field");

				if(key.StartsWith("@L")) {
					int id = int.Parse(key[2..]);
					translation.Add(id, CoalesceStrings(csvReader.GetField("Translation"), csvReader.GetField("Original")));
					speakers.Add(id, CoalesceStrings(csvReader.GetField("Speaker")));
				}
				else if(key.StartsWith("@N")) {
					names.Add(int.Parse(key[2..]), CoalesceStrings(csvReader.GetField("Translation"), csvReader.GetField("Original")));
				}
			}

			script.Translations = translation;
			script.Speakers = speakers;
			script.Names = names;
			return script;
		}

		public static string CoalesceStrings(params string?[] values) {
			foreach(var value in values) {
				if(!string.IsNullOrEmpty(value))
					return value;
			}
			return "";
		}

		public void WriteCode(string fileName) {
			File.WriteAllLines(fileName, Lines.Select(line => line.Text), Encoding.GetEncoding(932));
		}

		public void WriteToTxt(string txtFileName) {
			Internalize();
			WriteCode(txtFileName);

			foreach(var section in Sections) {
				section.Save();
			}
		}

		public void WriteToCsv(string csvFileName, string metaFileName) {
			Externalize();
			WriteCode(metaFileName);

			using var csvWriter = new CsvWriter(File.CreateText(csvFileName), CultureInfo.InvariantCulture);

			csvWriter.WriteField("ID");
			csvWriter.WriteField("Speaker");
			csvWriter.WriteField("Original");
			csvWriter.WriteField("Translation");
			csvWriter.NextRecord();

			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.NextRecord();

			csvWriter.WriteField("Names");
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.NextRecord();

			foreach(var pair in Names!) {
				csvWriter.WriteField("@N" + pair.Key);
				csvWriter.WriteField(null);
				csvWriter.WriteField(pair.Value);
				csvWriter.WriteField(null);
				csvWriter.NextRecord();
			}

			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.NextRecord();

			csvWriter.WriteField("Lines");
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.WriteField(null);
			csvWriter.NextRecord();

			foreach(var pair in Translations!) {
				csvWriter.WriteField("@L" + pair.Key);
				csvWriter.WriteField(Speakers!.TryGetValue(pair.Key, out var speaker) ? speaker : null);
				csvWriter.WriteField(pair.Value);
				csvWriter.WriteField(null);
				csvWriter.NextRecord();
			}
		}

		public static LineType GetLineType(string line) {
			if(line.StartsWith("***"))
				return LineType.Section;

			if(string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//"))
				return LineType.None;

			if(line.Contains('_') || line.Contains("//"))
				return LineType.Command;

			if(line.StartsWith("@N") || NamePattern.IsMatch(line))
				return LineType.Speaker;

			return LineType.Text;
		}

		public class TextLine {
			public string? Speaker;
			public string Text;

			public TextLine(string? speaker, string text) {
				Speaker = speaker;
				Text = text;
			}

			public void Append(string line) {
				Text += Environment.NewLine + line;
			}

			public override string ToString() => Speaker != null ? Speaker + ": \"" + Text + "\"" : Text;
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

public class SptEntry {
	public SptEntryType Type;
	public int Field04;
	public int Field08;
	public int Field0C;
	public int Field10;
	public int Field14;
	public int Field18;
	public int Field1C;
}

public enum SptEntryType {
	Text = 0x01,        // {text}
	BgIn = 0x07,        // BG_BGM{id}_FIN
	BgOut = 0x08,       // BG_BGM{id}_FOUT
	Se = 0x0A,          // SE_{id}
	EfB = 0x13,         // EF_B{id}_{file}
	EfC = 0x14,         // EF_C{id}_{file}
	EfWait = 0x1D,      // EF_WAIT_{delay}
	EfFlag = 0x21,      // EF_FLAG_{id}_{value}
	EfSkip = 0x24,      // EF_SKIP
	BgCv = 0x29,        // BGCV_{OFF|id}

	// 0A, 0D, 12, 02, 04, 25, 1D, 03, 0B, 28, 22, 27, 18, 23, 26, 0F, 06, 0E, 1A
}

public enum SptEntryType2 {
	Bg = 1,     // background music
	BgCv = 2,   // background character voice
	Ef = 4,     // set layer content?
	EfFlag = 5, // set flag
	Text = 7,   // output text
}

public class ScriptSection {
	public SptEntry[] Entries;
	public readonly string Path;
	public readonly List<ScriptCommand> Commands = new();

	public ScriptSection(string path) {
		Path = path;
		using var reader = new BinaryReader(File.OpenRead(path));
		int count = reader.ReadInt32();
		Entries = new SptEntry[count];

		for(int i = 0; i < count; i++) {
			Entries[i] = new SptEntry {
				Type = (SptEntryType)reader.ReadInt32(),
				Field04 = reader.ReadInt32(),
				Field08 = reader.ReadInt32(),
				Field0C = reader.ReadInt32(),
				Field10 = reader.ReadInt32(),
				Field14 = reader.ReadInt32(),
				Field18 = reader.ReadInt32(),
				Field1C = reader.ReadInt32(),
			};
		}
	}

	public void Save() {
		using var writer = new BinaryWriter(File.OpenWrite(Path));

		writer.Write(Entries.Length);
		foreach(var entry in Entries) {
			writer.Write((int)entry.Type);
			writer.Write(entry.Field04);
			writer.Write(entry.Field08);
			writer.Write(entry.Field0C);
			writer.Write(entry.Field10);
			writer.Write(entry.Field14);
			writer.Write(entry.Field18);
			writer.Write(entry.Field1C);
		}
	}
}

public class ScriptLine {
	public string Text;
	public int LineNumber;
	public readonly LineType Type;
	public readonly ScriptCommand? Command;

	public ScriptLine(LineType type, string text, int lineNumber, ScriptCommand? command = null) {
		Type = type;
		Text = text;
		LineNumber = lineNumber;
		Command = command;
		if(command != null) command.SourceLine = this;
	}

	public override string ToString() => $"({LineNumber}) {Type}: {Text}";
}

public class ScriptCommand {
	public readonly ScriptSection Section;
	public readonly int CommandIndex;
	public ScriptLine SourceLine = null!;

	public SptEntry SptEntry => Section.Entries[CommandIndex];

	public ScriptCommand(ScriptSection section, int commandIndex) {
		section.Commands.Add(this);
		Section = section;
		CommandIndex = commandIndex;
	}
}

public class TextCommand : ScriptCommand {
	public ScriptLine? NameLine;
	public readonly List<ScriptLine> ContinuationLines = new();

	public TextCommand(ScriptSection section, int commandIndex) : base(section, commandIndex) { }

	public void AddContinuation(ScriptLine line) {
		ContinuationLines.Add(line);
	}
}

public enum LineType {
	None, Text, TextContinuation, Speaker, Command, Section
}

//0A 00 00 00 00 00 00 00 00 00 00 00 FF FF FF FF FF FF FF FF 00 00 00 00 02 00 00 00 3E 00 00 00
//0D 00 00 00 00 00 00 00 00 00 00 00 FF FF FF FF FF FF FF FF 00 00 00 00 02 00 00 00 3E 00 00 00