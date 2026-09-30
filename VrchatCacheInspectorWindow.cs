// Assets/Editor/VrchatCacheInspectorWindow.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class VrchatCacheInspectorWindow : EditorWindow
{
    private const uint CompressionTypeMask = 0x3F;
    private const uint BlocksInfoAtEnd = 0x80;
    private const uint BlockInfoNeedPaddingAtStart = 0x200;

    // Observed VRChat cache chunk layout:
    // 12-byte nonce/header
    // 4-byte payload length
    // payload
    // 16-byte trailing authentication data
    //
    // Maximum physical chunk:
    // 65536 payload + 32 overhead
    private const int CacheChunkOverhead = 32;
    private const int CacheChunkPayloadMax = 65536;
    private const int CacheChunkPhysicalMax =
        CacheChunkPayloadMax + CacheChunkOverhead;

    private string _filePath = string.Empty;
    private Vector2 _scroll;
    private string _report = "Select a __data file.";

    [MenuItem("Tools/VRChat/Cache Inspector")]
    private static void Open()
    {
        GetWindow<VrchatCacheInspectorWindow>(
            "VRChat Cache Inspector"
        );
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "VRChat UnityFS Cache Inspector",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.TextField(
                "File",
                _filePath
            );

            if (GUILayout.Button(
                    "Select",
                    GUILayout.Width(80)))
            {
                SelectFile();
            }
        }

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(
                   string.IsNullOrEmpty(_filePath)))
        {
            if (GUILayout.Button("Analyze"))
            {
                Analyze();
            }

            if (GUILayout.Button("Save Report"))
            {
                SaveReport();
            }
        }

        EditorGUILayout.Space();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.TextArea(
            _report,
            GUILayout.ExpandHeight(true)
        );

        EditorGUILayout.EndScrollView();
    }

    private void SelectFile()
    {
        string path = EditorUtility.OpenFilePanel(
            "Select VRChat cache __data",
            "",
            ""
        );

        if (string.IsNullOrEmpty(path))
            return;

        _filePath = path;
        _report = "Ready: " + path;
    }

    private void Analyze()
    {
        try
        {
            _report = AnalyzeFile(_filePath);
        }
        catch (Exception ex)
        {
            _report =
                "Analysis failed.\n\n" +
                ex;
        }
    }

    private void SaveReport()
    {
        if (string.IsNullOrEmpty(_report))
            return;

        string path = EditorUtility.SaveFilePanel(
            "Save analysis report",
            "",
            "vrchat_cache_report.txt",
            "txt"
        );

        if (string.IsNullOrEmpty(path))
            return;

        File.WriteAllText(
            path,
            _report,
            new UTF8Encoding(false)
        );

        Debug.Log(
            $"[VRChat Cache Inspector] Saved: {path}"
        );
    }

    private static string AnalyzeFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );

        var sb = new StringBuilder();

        UnityFsHeader header = ReadHeader(stream);

        sb.AppendLine("=== UnityFS Header ===");
        sb.AppendLine($"File            : {path}");
        sb.AppendLine($"Actual File Size: {stream.Length:N0}");
        sb.AppendLine($"Signature       : {header.Signature}");
        sb.AppendLine($"Format Version  : {header.FormatVersion}");
        sb.AppendLine($"Unity Version   : {header.UnityVersion}");
        sb.AppendLine($"Unity Revision  : {header.UnityRevision}");
        sb.AppendLine($"Bundle Size     : {header.BundleSize:N0}");
        sb.AppendLine(
            $"BlocksInfo CSize: {header.CompressedBlocksInfoSize:N0}"
        );
        sb.AppendLine(
            $"BlocksInfo USize: {header.UncompressedBlocksInfoSize:N0}"
        );
        sb.AppendLine(
            $"Flags           : 0x{header.Flags:X8}"
        );

        if (header.Signature != "UnityFS")
        {
            sb.AppendLine();
            sb.AppendLine(
                "Not a UnityFS AssetBundle."
            );

            return sb.ToString();
        }

        long positionAfterHeader = stream.Position;

        if (header.FormatVersion >= 7)
        {
            positionAfterHeader =
                Align(positionAfterHeader, 16);
        }

        int blocksInfoCompression =
            (int)(header.Flags & CompressionTypeMask);

        sb.AppendLine(
            $"BlocksInfo Compression: " +
            CompressionName(blocksInfoCompression)
        );

        long blocksInfoPosition;

        bool blocksInfoAtEnd =
            (header.Flags & BlocksInfoAtEnd) != 0;

        if (blocksInfoAtEnd)
        {
            blocksInfoPosition =
                (long)header.BundleSize -
                header.CompressedBlocksInfoSize;
        }
        else
        {
            blocksInfoPosition = positionAfterHeader;
        }

        sb.AppendLine(
            $"BlocksInfo Position: 0x{blocksInfoPosition:X}"
        );

        // This inspector intentionally does not implement
        // LZMA/LZ4 decompression of BlocksInfo.
        //
        // The VRChat cache sample inspected previously used
        // CompressionType.None for BlocksInfo.
        if (blocksInfoCompression != 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "BlocksInfo itself is compressed."
            );
            sb.AppendLine(
                "This inspector currently parses " +
                "uncompressed BlocksInfo only."
            );

            return sb.ToString();
        }

        byte[] blocksInfoBytes =
            ReadExactAt(
                stream,
                blocksInfoPosition,
                checked(
                    (int)header.CompressedBlocksInfoSize
                )
            );

        BlocksInfo blocksInfo =
            ParseBlocksInfo(blocksInfoBytes);

        sb.AppendLine();
        sb.AppendLine("=== Blocks ===");
        sb.AppendLine(
            $"Block Count: {blocksInfo.Blocks.Count}"
        );

        long dataStart;

        if (blocksInfoAtEnd)
        {
            dataStart = positionAfterHeader;
        }
        else
        {
            dataStart =
                blocksInfoPosition +
                header.CompressedBlocksInfoSize;
        }

        if ((header.Flags &
             BlockInfoNeedPaddingAtStart) != 0)
        {
            dataStart = Align(dataStart, 16);
        }

        sb.AppendLine(
            $"Data Start : 0x{dataStart:X}"
        );

        long currentBlockOffset = dataStart;

        int protectedBlockCount = 0;

        for (int i = 0;
             i < blocksInfo.Blocks.Count;
             i++)
        {
            StorageBlock block =
                blocksInfo.Blocks[i];

            bool cacheWrapped =
                DetectCacheChunkFraming(
                    stream,
                    currentBlockOffset,
                    block.CompressedSize,
                    out int chunkCount
                );

            if (cacheWrapped)
                protectedBlockCount++;

            sb.AppendLine();
            sb.AppendLine($"Block #{i}");
            sb.AppendLine(
                $"  Offset      : 0x{currentBlockOffset:X}"
            );
            sb.AppendLine(
                $"  Compressed  : {block.CompressedSize:N0}"
            );
            sb.AppendLine(
                $"  Uncompressed: {block.UncompressedSize:N0}"
            );
            sb.AppendLine(
                $"  Flags       : 0x{block.Flags:X4}"
            );
            sb.AppendLine(
                $"  Compression : " +
                CompressionName(
                    block.Flags & 0x3F
                )
            );
            sb.AppendLine(
                $"  Cache Frame : " +
                (cacheWrapped
                    ? $"Detected ({chunkCount} chunks)"
                    : "Not detected")
            );

            currentBlockOffset +=
                block.CompressedSize;
        }

        sb.AppendLine();
        sb.AppendLine("=== Directory ===");

        foreach (DirectoryNode node
                 in blocksInfo.Nodes)
        {
            sb.AppendLine(
                $"{node.Path}"
            );
            sb.AppendLine(
                $"  Offset: {node.Offset:N0}"
            );
            sb.AppendLine(
                $"  Size  : {node.Size:N0}"
            );
            sb.AppendLine(
                $"  Flags : 0x{node.Flags:X8}"
            );
        }

        sb.AppendLine();
        sb.AppendLine("=== Summary ===");

        sb.AppendLine(
            $"Storage Blocks : {blocksInfo.Blocks.Count}"
        );

        sb.AppendLine(
            $"Cache-wrapped  : {protectedBlockCount}"
        );

        sb.AppendLine(
            $"Directory Files: {blocksInfo.Nodes.Count}"
        );

        if (protectedBlockCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "The bundle contains additional cache " +
                "framing inside one or more UnityFS blocks."
            );

            sb.AppendLine(
                "Passing those bytes directly to a normal " +
                "LZ4 decoder will therefore fail."
            );
        }

        return sb.ToString();
    }

    private static UnityFsHeader ReadHeader(
        Stream stream)
    {
        stream.Position = 0;

        string signature =
            ReadNullTerminatedString(stream);

        uint formatVersion =
            ReadUInt32BE(stream);

        string unityVersion =
            ReadNullTerminatedString(stream);

        string unityRevision =
            ReadNullTerminatedString(stream);

        ulong bundleSize =
            ReadUInt64BE(stream);

        uint compressedBlocksInfoSize =
            ReadUInt32BE(stream);

        uint uncompressedBlocksInfoSize =
            ReadUInt32BE(stream);

        uint flags =
            ReadUInt32BE(stream);

        return new UnityFsHeader
        {
            Signature = signature,
            FormatVersion = formatVersion,
            UnityVersion = unityVersion,
            UnityRevision = unityRevision,
            BundleSize = bundleSize,
            CompressedBlocksInfoSize =
                compressedBlocksInfoSize,
            UncompressedBlocksInfoSize =
                uncompressedBlocksInfoSize,
            Flags = flags
        };
    }

    private static BlocksInfo ParseBlocksInfo(
        byte[] bytes)
    {
        using var stream =
            new MemoryStream(bytes, false);

        // UnityFS uncompressed data hash.
        byte[] hash = new byte[16];

        ReadExactly(stream, hash, 0, hash.Length);

        int blockCount =
            ReadInt32BE(stream);

        if (blockCount < 0 ||
            blockCount > 100000)
        {
            throw new InvalidDataException(
                $"Invalid block count: {blockCount}"
            );
        }

        var blocks =
            new List<StorageBlock>(blockCount);

        for (int i = 0;
             i < blockCount;
             i++)
        {
            blocks.Add(
                new StorageBlock
                {
                    UncompressedSize =
                        ReadUInt32BE(stream),

                    CompressedSize =
                        ReadUInt32BE(stream),

                    Flags =
                        ReadUInt16BE(stream)
                }
            );
        }

        int nodeCount =
            ReadInt32BE(stream);

        if (nodeCount < 0 ||
            nodeCount > 100000)
        {
            throw new InvalidDataException(
                $"Invalid node count: {nodeCount}"
            );
        }

        var nodes =
            new List<DirectoryNode>(nodeCount);

        for (int i = 0;
             i < nodeCount;
             i++)
        {
            nodes.Add(
                new DirectoryNode
                {
                    Offset =
                        ReadInt64BE(stream),

                    Size =
                        ReadInt64BE(stream),

                    Flags =
                        ReadUInt32BE(stream),

                    Path =
                        ReadNullTerminatedString(stream)
                }
            );
        }

        return new BlocksInfo
        {
            Blocks = blocks,
            Nodes = nodes
        };
    }

    /// <summary>
    /// Detects the additional framing observed in
    /// VRChat's cached UnityFS blocks.
    ///
    /// This only validates the structure.
    /// It does NOT decrypt or modify the payload.
    /// </summary>
    private static bool DetectCacheChunkFraming(
        FileStream stream,
        long blockOffset,
        uint compressedSize,
        out int chunkCount)
    {
        chunkCount = 0;

        long remaining = compressedSize;
        long cursor = blockOffset;

        if (remaining < CacheChunkOverhead)
            return false;

        while (remaining > 0)
        {
            int physicalSize =
                (int)Math.Min(
                    remaining,
                    CacheChunkPhysicalMax
                );

            if (physicalSize <
                CacheChunkOverhead)
            {
                return false;
            }

            // Need only the first 16 bytes:
            //
            // [0..11] nonce/header
            // [12..15] little-endian payload length
            byte[] header =
                ReadExactAt(
                    stream,
                    cursor,
                    16
                );

            int payloadLength =
                ReadInt32LE(
                    header,
                    12
                );

            int expectedPayloadLength =
                physicalSize -
                CacheChunkOverhead;

            if (payloadLength < 0)
                return false;

            if (payloadLength !=
                expectedPayloadLength)
            {
                return false;
            }

            cursor += physicalSize;
            remaining -= physicalSize;
            chunkCount++;
        }

        return chunkCount > 0;
    }

    private static string CompressionName(
        int compression)
    {
        return compression switch
        {
            0 => "None",
            1 => "LZMA",
            2 => "LZ4",
            3 => "LZ4HC",
            _ => $"Unknown ({compression})"
        };
    }

    private static byte[] ReadExactAt(
        FileStream stream,
        long offset,
        int count)
    {
        byte[] buffer =
            new byte[count];

        stream.Position = offset;

        ReadExactly(
            stream,
            buffer,
            0,
            count
        );

        return buffer;
    }

    private static void ReadExactly(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        int total = 0;

        while (total < count)
        {
            int read = stream.Read(
                buffer,
                offset + total,
                count - total
            );

            if (read <= 0)
                throw new EndOfStreamException();

            total += read;
        }
    }

    private static string ReadNullTerminatedString(
        Stream stream)
    {
        using var buffer =
            new MemoryStream();

        while (true)
        {
            int value = stream.ReadByte();

            if (value < 0)
                throw new EndOfStreamException();

            if (value == 0)
                break;

            buffer.WriteByte((byte)value);
        }

        return Encoding.UTF8.GetString(
            buffer.ToArray()
        );
    }

    private static ushort ReadUInt16BE(
        Stream stream)
    {
        int a = stream.ReadByte();
        int b = stream.ReadByte();

        if ((a | b) < 0)
            throw new EndOfStreamException();

        return (ushort)(
            (a << 8) |
            b
        );
    }

    private static uint ReadUInt32BE(
        Stream stream)
    {
        uint a = ReadByteChecked(stream);
        uint b = ReadByteChecked(stream);
        uint c = ReadByteChecked(stream);
        uint d = ReadByteChecked(stream);

        return
            (a << 24) |
            (b << 16) |
            (c << 8) |
            d;
    }

    private static int ReadInt32BE(
        Stream stream)
    {
        return unchecked(
            (int)ReadUInt32BE(stream)
        );
    }

    private static ulong ReadUInt64BE(
        Stream stream)
    {
        ulong high =
            ReadUInt32BE(stream);

        ulong low =
            ReadUInt32BE(stream);

        return
            (high << 32) |
            low;
    }

    private static long ReadInt64BE(
        Stream stream)
    {
        return unchecked(
            (long)ReadUInt64BE(stream)
        );
    }

    private static uint ReadByteChecked(
        Stream stream)
    {
        int value = stream.ReadByte();

        if (value < 0)
            throw new EndOfStreamException();

        return (uint)value;
    }

    private static int ReadInt32LE(
        byte[] bytes,
        int offset)
    {
        return
            bytes[offset] |
            (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) |
            (bytes[offset + 3] << 24);
    }

    private static long Align(
        long value,
        int alignment)
    {
        long mask = alignment - 1;

        return
            (value + mask) &
            ~mask;
    }

    private sealed class UnityFsHeader
    {
        public string Signature;
        public uint FormatVersion;
        public string UnityVersion;
        public string UnityRevision;
        public ulong BundleSize;
        public uint CompressedBlocksInfoSize;
        public uint UncompressedBlocksInfoSize;
        public uint Flags;
    }

    private sealed class StorageBlock
    {
        public uint UncompressedSize;
        public uint CompressedSize;
        public ushort Flags;
    }

    private sealed class DirectoryNode
    {
        public long Offset;
        public long Size;
        public uint Flags;
        public string Path;
    }

    private sealed class BlocksInfo
    {
        public List<StorageBlock> Blocks;
        public List<DirectoryNode> Nodes;
    }
}