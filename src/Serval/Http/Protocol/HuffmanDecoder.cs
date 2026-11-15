#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Protocol;

internal static class HuffmanDecoder
{
    private static readonly Node Root = BuildTree();

    public static byte[] Decode(ReadOnlySpan<byte> encoded, int maximum)
    {
        var output  = new List<byte>((int)Math.Min((long)encoded.Length * 2, maximum));
        var node    = Root;
        var padding = 0;
        var allOnes = true;

        foreach (var value in encoded)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var one = (value & 1 << bit) != 0;

                padding++;
                allOnes &= one;

                node = (one ? node.One : node.Zero) ?? throw Invalid();
                if (node.Symbol < 0)
                {
                    continue;
                }

                if (node.Symbol == 256 || output.Count >= maximum)
                {
                    throw Invalid();
                }

                output.Add((byte)node.Symbol);

                node    = Root;
                padding = 0;
                allOnes = true;
            }
        }

        // HPACK/QPACK pad the last byte with a prefix of the all-ones EOS code, never a full symbol.
        if (node != Root && (padding > 7 || !allOnes))
        {
            throw Invalid();
        }

        return [.. output];
    }

    private static Node BuildTree()
    {
        var root = new Node();
        for (var symbol = 0; symbol < HuffmanCodes.Values.Length; symbol++)
        {
            var (code, length) = HuffmanCodes.Values[symbol];
            var node = root;
            for (var bit = length - 1; bit >= 0; bit--)
            {
                node = (code & 1u << bit) == 0 ? node.Zero ??= new Node() : node.One ??= new Node();
            }

            node.Symbol = symbol;
        }

        return root;
    }

    private static InvalidDataException Invalid() => new("Invalid Huffman string.");

    private sealed class Node
    {
        public Node? One;
        public int   Symbol = -1;
        public Node? Zero;
    }
}
