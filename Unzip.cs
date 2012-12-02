// Unzip class for .NET 3.5
// Written by Alexey Yakovlev <yallie@yandex.ru>

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Internals
{
	/// <summary>
	/// Unzip helper class.
	/// </summary>
	public class Unzip : IDisposable
	{
		private const int EntrySignature = 0x02014B50;

		private const int FileSignature = 0x04034b50;

		private const int DirectorySignature = 0x06054B50;

		private const int BufferSize = 16 * 1024;

		/// <summary>
		/// Zip archive entry.
		/// </summary>
		public class Entry
		{
			public string Name { get; set; }

			public string Comment { get; set; }

			public int Crc32 { get; set; }

			public int CompressedSize { get; set; }

			public int OriginalSize { get; set; }

			public int HeaderOffset { get; set; }

			public int DataOffset { get; set; }

			public bool Deflated { get; set; }

			public bool IsDirectory { get { return Name.EndsWith("/"); } }

			public bool IsFile { get { return !IsDirectory; } }
		}

		public Unzip(string fileName)
			: this(File.OpenRead(fileName))
		{
		}

		public Unzip(Stream stream)
		{
			Stream = stream;
			Reader = new BinaryReader(Stream);
		}

		private Stream Stream { get; set; }

		private BinaryReader Reader { get; set; }

		public void Dispose()
		{
			if (Stream != null)
			{
				Stream.Dispose();
				Stream = null;
			}

			if (Reader != null)
			{
				Reader.Close();
				Reader = null;
			}
		}

		public void ExtractToDirectory(string directoryName)
		{
			foreach (var entry in Entries.Where(e => !e.IsDirectory))
			{
				// create target directory with file folder
				var fileName = Path.Combine(directoryName, entry.Name);
				var dirName = Path.GetDirectoryName(fileName);
				Directory.CreateDirectory(dirName);

				// save file
				using (var outStream = File.Create(fileName))
				{
					Extract(entry.Name, outStream);
				}
			}
		}

		public void Extract(string fileName, Stream outputStream)
		{
			var entry = Entries.Where(e => e.Name == fileName).First();
			Extract(entry, outputStream);
		}

		public void Extract(Entry entry, Stream outputStream)
		{
			// check file signature
			Stream.Seek(entry.HeaderOffset, SeekOrigin.Begin);
			if (Reader.ReadInt32() != FileSignature)
			{
				throw new InvalidOperationException("File signature don't match.");
			}

			// move to file data
			Stream.Seek(entry.DataOffset, SeekOrigin.Begin);
			var inputStream = Stream;
			if (entry.Deflated)
			{
				inputStream = new DeflateStream(Stream, CompressionMode.Decompress, true);
			}

			// allocate buffer
			var count = entry.OriginalSize;
			var bufferSize = Math.Min(BufferSize, entry.OriginalSize);
			var buffer = new byte[bufferSize];

			while (count > 0)
			{
				// decompress data
				var read = inputStream.Read(buffer, 0, bufferSize);
				if (read == 0)
				{
					break;
				}

				// copy to the output stream
				outputStream.Write(buffer, 0, read);
				count -= read;
			}
		}

		public IEnumerable<string> FileNames
		{
			get
			{
				return Entries.Select(e => e.Name).Where(f => !f.EndsWith("/"));
			}
		}

		private Entry[] entries;

		public IEnumerable<Entry> Entries
		{
			get
			{
				if (entries == null)
				{
					entries = ReadZipEntries().ToArray();
				}

				return entries;
			}
		}

		private IEnumerable<Entry> ReadZipEntries()
		{
			if (Stream.Length < 22)
			{
				yield break;
			}

			Stream.Seek(-22, SeekOrigin.End);

			// find directory signature
			while (Reader.ReadInt32() != DirectorySignature)
			{
				if (Stream.Position <= 5)
				{
					yield break;
				}

				// move 1 byte back
				Stream.Seek(-5, SeekOrigin.Current);
			}

			// read directory properties
			Stream.Seek(6, SeekOrigin.Current);
			var entries = Reader.ReadUInt16();
			var difSize = Reader.ReadInt32();
			var dirOffset = Reader.ReadUInt32();
			Stream.Seek(dirOffset, SeekOrigin.Begin);

			// read directory entries
			for (int i = 0; i < entries; i++)
			{
				if (Reader.ReadInt32() != EntrySignature)
				{
					continue;
				}

				// read file properties
				Reader.ReadInt32();
				bool utf8 = (Reader.ReadInt16() & 0x0800) != 0;
				short method = Reader.ReadInt16();
				int timestamp = Reader.ReadInt32();
				int crc32 = Reader.ReadInt32();
				int compressedSize = Reader.ReadInt32();
				int fileSize = Reader.ReadInt32();
				short fileNameSize = Reader.ReadInt16();
				short extraSize = Reader.ReadInt16();
				short commentSize = Reader.ReadInt16();
				int headerOffset = Reader.ReadInt32();
				Reader.ReadInt32();
				int fileHeaderOffset = Reader.ReadInt32();
				var fileNameBytes = Reader.ReadBytes(fileNameSize);
				Stream.Seek(extraSize, SeekOrigin.Current);
				var fileCommentBytes = Reader.ReadBytes(commentSize);
				var fileDataOffset = CalculateFileDataOffset(fileHeaderOffset);

				// decode zip file entry
				var encoder = utf8 ? Encoding.UTF8 : Encoding.Default;
				yield return new Entry
				{
					Name = encoder.GetString(fileNameBytes),
					Comment = encoder.GetString(fileCommentBytes),
					Crc32 = crc32,
					CompressedSize = compressedSize,
					OriginalSize = fileSize,
					HeaderOffset = fileHeaderOffset,
					DataOffset = fileDataOffset,
					Deflated = method == 8
				};
			}
		}

		private int CalculateFileDataOffset(int fileHeaderOffset)
		{
			var position = Stream.Position;
			Stream.Seek(fileHeaderOffset + 26, SeekOrigin.Begin);
			var fileNameSize = Reader.ReadInt16();
			var extraSize = Reader.ReadInt16();

			var fileOffset = (int)Stream.Position + fileNameSize + extraSize;
			Stream.Seek(position, SeekOrigin.Begin);
			return fileOffset;
		}
	}
}