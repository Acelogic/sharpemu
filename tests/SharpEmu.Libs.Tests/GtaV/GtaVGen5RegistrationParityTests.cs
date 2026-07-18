// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.GtaV;

public sealed class GtaVGen5RegistrationParityTests
{
    private const string InventoryResourceName = "SharpEmu.Libs.Tests.GtaVGen5NidInventory.csv";
    private const string InventorySha256 = "efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a";
    private const string LegacyStackGuardNid = "f7uOxY9mM1U";

    [Fact]
    public void PinnedInventory_HasExactCompiledCallableAndDataRegistrationParity()
    {
        var inventoryBytes = ReadInventory();
        Assert.Equal(InventorySha256, Convert.ToHexString(SHA256.HashData(inventoryBytes)).ToLowerInvariant());

        var inventory = ParseInventory(Encoding.UTF8.GetString(inventoryBytes));
        var catalogNids = inventory.Select(row => row.Nid).ToHashSet(StringComparer.Ordinal);
        var functionNids = inventory
            .Where(row => row.SymbolKind == "function")
            .Select(row => row.Nid)
            .ToHashSet(StringComparer.Ordinal);
        var objectNids = inventory
            .Where(row => row.SymbolKind == "object")
            .Select(row => row.Nid)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1_432, inventory.Count);
        Assert.Equal(1_432, catalogNids.Count);
        Assert.Equal(1_426, functionNids.Count);
        Assert.Equal(6, objectNids.Count);
        Assert.Contains(LegacyStackGuardNid, objectNids);

        var callable = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => catalogNids.Contains(export.Nid))
            .ToArray();
        var callableNids = callable.Select(export => export.Nid).ToHashSet(StringComparer.Ordinal);
        var expectedCallableNids = functionNids.Append(LegacyStackGuardNid).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(1_427, callable.Length);
        Assert.Equal(1_427, callableNids.Count);
        AssertSetEqual(expectedCallableNids, callableNids, "callable");

        var data = DataSymbolRegistry.CreateRegistrations(Generation.Gen5);
        var dataNids = data.Select(registration => registration.Nid).ToHashSet(StringComparer.Ordinal);
        var expectedDataNids = objectNids
            .Where(nid => nid != LegacyStackGuardNid)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(5, data.Count);
        Assert.Equal(5, dataNids.Count);
        AssertSetEqual(expectedDataNids, dataNids, "data");
        Assert.Empty(DataSymbolRegistry.CreateRegistrations(Generation.Gen4));

        Assert.Empty(callableNids.Intersect(dataNids, StringComparer.Ordinal));
        AssertSetEqual(catalogNids, callableNids.Union(dataNids, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal), "union");

        var allGen5CallableNids = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Select(export => export.Nid)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(dataNids.Intersect(allGen5CallableNids, StringComparer.Ordinal));
    }

    private static byte[] ReadInventory()
    {
        using var stream = typeof(GtaVGen5RegistrationParityTests).Assembly
            .GetManifestResourceStream(InventoryResourceName)
            ?? throw new InvalidOperationException($"Missing embedded inventory '{InventoryResourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IReadOnlyList<InventoryRow> ParseInventory(string csv)
    {
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("GTA V inventory is empty.");
        var header = ParseCsvLine(headerLine);
        var nidIndex = header.IndexOf("nid");
        var symbolKindIndex = header.IndexOf("symbol_kinds");
        if (nidIndex < 0 || symbolKindIndex < 0)
        {
            throw new InvalidDataException("GTA V inventory is missing nid or symbol_kinds.");
        }

        var rows = new List<InventoryRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Count != header.Count)
            {
                throw new InvalidDataException(
                    $"GTA V inventory row {rows.Count + 2} has {fields.Count} fields; expected {header.Count}.");
            }

            var symbolKind = fields[symbolKindIndex];
            if (symbolKind is not ("function" or "object"))
            {
                throw new InvalidDataException(
                    $"GTA V inventory row {rows.Count + 2} has unsupported symbol kind '{symbolKind}'.");
            }

            rows.Add(new InventoryRow(fields[nidIndex], symbolKind));
        }

        return rows;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("GTA V inventory contains an unterminated quoted field.");
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static void AssertSetEqual(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual,
        string label)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"GTA V {label} registration mismatch. Missing=[{string.Join(",", missing)}] Extra=[{string.Join(",", extra)}]");
    }

    private sealed record InventoryRow(string Nid, string SymbolKind);
}
