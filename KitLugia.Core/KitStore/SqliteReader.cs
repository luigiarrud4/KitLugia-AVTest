using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KitLugia.Core.KitStore
{
    /// <summary>
    /// Leitor SQLite read-only mínimo e sem dependências — usado só para ler o index.db do winget.
    /// Suporta apenas SELECT de colunas TEXT/INTEGER de tabelas b-tree leaf (table scan).
    /// Recupera também: id, name, moniker, latest_version da tabela "packages".
    /// </summary>
    internal static class SqliteReader
    {
        public static List<object?[]> ReadAll(string dbPath, string table)
        {
            var result = new List<object?[]>(16384);
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var header = new byte[100];
            fs.Read(header, 0, 100);
            if (Encoding.ASCII.GetString(header, 0, 16) != "SQLite format 3\u0000")
                throw new InvalidDataException("Não é um arquivo SQLite válido");

            int pageSize = (header[16] << 8) | header[17];
            if (pageSize == 1) pageSize = 65536;

            long rootPage = FindTableRootPage(fs, pageSize, table);
            WalkTable(fs, pageSize, rootPage, result);
            return result;
        }

        private static long FindTableRootPage(FileStream fs, int pageSize, string table)
        {
            var page = ReadPage(fs, 1, pageSize);
            // Página 1: b-tree header começa no byte 100 (após o file header).
            var cells = ReadCells(page, 100);
            foreach (var cell in cells)
            {
                var rec = ParseRecord(cell);
                if (rec.Count >= 4 && (rec[0] as string) == "table" &&
                    string.Equals(rec[1] as string, table, StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToInt64(rec[3]);
                }
                if (rec.Count >= 4 && (rec[0] as string) == "table" &&
                    string.Equals(rec[2] as string, table, StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToInt64(rec[3]);
                }
            }
            throw new InvalidDataException($"Tabela '{table}' não encontrada");
        }

        private static void WalkTable(FileStream fs, int pageSize, long rootPage, List<object?[]> output)
        {
            var visited = new HashSet<long>();
            var stack = new Stack<long>();
            stack.Push(rootPage);
            while (stack.Count > 0)
            {
                var pg = stack.Pop();
                if (!visited.Add(pg)) continue;
                var page = ReadPage(fs, pg, pageSize);
                int headerOff = (pg == 1) ? 100 : 0;
                int type = page[headerOff];
                if (type == 0x0D) // leaf table
                {
                    foreach (var cell in ReadCells(page, headerOff))
                        output.Add(ParseRecord(cell).ToArray());
                }
                else if (type == 0x05) // interior table
                {
                    // cada célula: 4 bytes child page (big-endian) + chave (rowid). Nós só precisamos do child pointer.
                    foreach (var cell in ReadCells(page, headerOff))
                    {
                        if (cell.Length >= 4)
                        {
                            long child = ((long)cell[0] << 24) | ((long)cell[1] << 16) | ((long)cell[2] << 8) | cell[3];
                            if (child > 0) stack.Push(child);
                        }
                    }
                }
            }
        }

        private static byte[] ReadPage(FileStream fs, long pageNumber, int pageSize)
        {
            var buf = new byte[pageSize];
            fs.Position = (pageNumber - 1) * (long)pageSize;
            int read = 0;
            while (read < pageSize)
            {
                int n = fs.Read(buf, read, pageSize - read);
                if (n <= 0) break;
                read += n;
            }
            return buf;
        }

        // Lê o array de ponteiros de célula de uma página b-tree table e devolve o conteúdo de cada célula.
        // headerOff: offset onde começa o b-tree page header (100 na página 1, 0 nas demais).
        // IMPORTANTE: as células são armazenadas em ordem DEScrescente de endereço — o menor índice fica no endereço
        // mais alto. cell[i] ocupa [off[i+1], off[i]); a última célula ocupa [cellContentStart, off[last]).
        private static List<byte[]> ReadCells(byte[] page, int headerOff)
        {
            var result = new List<byte[]>();
            if (page.Length < headerOff + 8) return result;
            int numCells = (page[headerOff + 3] << 8) | page[headerOff + 4];
            int cellContentStart = (page[headerOff + 5] << 8) | page[headerOff + 6];
            int ptrArray = headerOff + 8;

            var offsets = new int[numCells];
            for (int i = 0; i < numCells; i++)
            {
                int p = ptrArray + i * 2;
                if (p + 1 >= page.Length) { numCells = i; break; }
                offsets[i] = (page[p] << 8) | page[p + 1];
            }

            for (int i = 0; i < numCells; i++)
            {
                int start, end;
                if (i + 1 < numCells)
                {
                    // célula i começa acima da célula i+1 (endereços descem conforme índice sobe)
                    start = offsets[i + 1];
                    end = offsets[i];
                }
                else
                {
                    start = cellContentStart;
                    end = offsets[i];
                }
                // proteção de bounds
                start = Math.Min(start, page.Length);
                end = Math.Max(end, 0);
                end = Math.Min(end, page.Length);
                if (end <= start || end > page.Length || start < 0) continue;
                var cell = new byte[end - start];
                Array.Copy(page, start, cell, 0, cell.Length);
                result.Add(cell);
            }
            return result;
        }

        // Decodifica um registro (payload) SQLite que ocupa os ÚLTIMOS payloadLen bytes de uma célula de tabela.
        // Célula de tabela leaf: [payloadLen varint][rowid varint][record payload de payloadLen bytes].
        private static List<object?> ParseRecord(byte[] cell)
        {
            var result = new List<object?>();
            if (cell.Length < 2) return result;
            int pos = 0;
            ulong payloadLen;
            pos += ReadVarint(cell, pos, out payloadLen);
            // (rowid = segundo varint; ignoramos e usamos o record no final da célula)
            long recStart = cell.Length - (long)payloadLen;
            if (recStart < 0) recStart = 0;
            var rec = new byte[cell.Length - recStart];
            Array.Copy(cell, recStart, rec, 0, rec.Length);

            int r2 = 0;
            ulong headerLenU;
            r2 += ReadVarint(rec, r2, out headerLenU);
            long headerLen = (long)headerLenU;
            long payloadEnd = rec.Length;

            var serialTypes = new List<ulong>();
            long consumedHeader = 0;
            int stPos = r2;
            while (stPos < rec.Length && consumedHeader < headerLen - 1)
            {
                ulong st;
                var used = ReadVarint(rec, stPos, out st);
                stPos += used;
                consumedHeader += used;
                serialTypes.Add(st);
                if (serialTypes.Count > 32) break;
            }
            // stPos agora aponta para o início dos valores
            for (int i = 0; i < serialTypes.Count; i++)
            {
                var val = DecodeValue(rec, ref stPos, serialTypes[i]);
                result.Add(val);
            }
            return result;
        }

        private static long SerialTypeLength(ulong st)
        {
            if (st == 0) return 0;
            if (st == 1) return 1;
            if (st == 2) return 2;
            if (st == 3) return 3;
            if (st == 4) return 4;
            if (st == 5) return 6;
            if (st == 6) return 8;
            if (st == 7) return 8;
            if (st == 8) return 0;
            if (st == 9) return 0;
            if (st >= 12 && (st & 1) == 0) return (long)((st - 12) / 2);
            if (st >= 13 && (st & 1) == 1) return (long)((st - 13) / 2);
            return 0;
        }

        private static object? DecodeValue(byte[] data, ref int pos, ulong st)
        {
            try
            {
                if (st == 0) return null;
                if (st == 1) { var v = (sbyte)data[pos]; pos += 1; return (long)v; }
                if (st == 2) { var v = (short)((data[pos] << 8) | data[pos + 1]); pos += 2; return (long)v; }
                if (st == 3) { long v = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2]; pos += 3; return v; }
                if (st == 4) { long v = ((long)data[pos] << 24) | ((long)data[pos + 1] << 16) | ((long)data[pos + 2] << 8) | data[pos + 3]; pos += 4; return v; }
                if (st == 5) { long v = 0; for (int i = 0; i < 6; i++) v = (v << 8) | data[pos + i]; pos += 6; return v; }
                if (st == 6) { long v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | data[pos + i]; pos += 8; return v; }
                if (st == 7) { var d = BitConverter.ToDouble(data, pos); pos += 8; return d; }
                if (st == 8) return 0L;
                if (st == 9) return 1L;
                if (st >= 13 && (st & 1) == 1)
                {
                    int len = (int)((st - 13) / 2);
                    var s = Encoding.UTF8.GetString(data, pos, len);
                    pos += len;
                    return s;
                }
                if (st >= 12 && (st & 1) == 0)
                {
                    int len = (int)((st - 12) / 2);
                    var b = new byte[len];
                    Array.Copy(data, pos, b, 0, len);
                    pos += len;
                    return b;
                }
                return null;
            }
            catch { return null; }
        }

        private static int ReadVarint(byte[] data, int pos, out ulong value)
        {
            ulong v = 0;
            for (int i = 0; i < 9; i++)
            {
                byte b = data[pos + i];
                v = (v << 7) | (ulong)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    value = v;
                    return i + 1;
                }
            }
            value = v;
            return 9;
        }
    }
}