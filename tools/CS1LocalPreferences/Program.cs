using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    const int MaximumBytes = 32 * 1024 * 1024;
    static string Game = "";
    static readonly UTF8Encoding Utf8 = new(false, true);
    static string Root = "";
    static int Main(string[] args)
    {
        try
        {
            Root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(Root, "scripts", "Enter-Project.ps1"))) Root = Directory.GetParent(Root)?.FullName ?? throw new Exception("Project root not found");
            if (args.Any(a => a != "--enable" && a != "--self-test")) throw new Exception("Only --enable or --self-test are supported");
            if (args.Contains("--self-test")) { SelfTest(); return 0; }
            Game = ResolveGame();
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colossal Order", "Cities_Skylines");
            string target = Path.Combine(local, "Addons", "Mods", "SC4Evolution");
            string file = Path.Combine(local, "userGameState.cgs");
            byte[] original = ReadBounded(file);
            var parsed = Parse(original);
            string workshop = Path.GetFullPath(Path.Combine(Game, "..", "..", "workshop", "content", "255710"));
            var knownPaths = Directory.GetDirectories(Path.Combine(Game, "Files", "Mods")).Concat(Directory.Exists(workshop) ? Directory.GetDirectories(workshop) : Array.Empty<string>()).Append(target).ToArray();
            int[] hashes = NativeHashes(knownPaths);
            for (int i = 0; i < hashes.Length; i++) if (hashes[i] != GameHash(knownPaths[i])) throw new Exception("Bundled Mono hash differs from decompiled game mscorlib");
            string key = Key(target, hashes[^1]);
            int matches = knownPaths.Take(knownPaths.Length - 1).Select((p,i) => Key(p, hashes[i])).Count(k => parsed.Bools.ContainsKey(k));
            Console.WriteLine("Runtime: " + Path.Combine(Game, "Mono", "bin", "mono.exe"));
            Console.WriteLine("Hash source: selected System.String.GetHashCode from Cities_Data/Managed/mscorlib.dll; native Mono cross-check passed");
            Console.WriteLine("Existing known installed mod key matches: " + matches);
            Console.WriteLine("Target path: " + target);
            Console.WriteLine("Target hash: " + hashes[^1]);
            Console.WriteLine("Target key: " + key);
            Console.WriteLine("Target enabled: " + (parsed.Bools.TryGetValue(key, out var value) ? original[value] != 0 : false) + (parsed.Bools.ContainsKey(key) ? " (stored)" : " (absent; default false)"));
            Console.WriteLine("Original SHA256: " + Sha(original));
            if (!args.Contains("--enable")) { Console.WriteLine("Inspect only; no preferences changed."); return 0; }
            RefuseRunning();
            if (matches < 3) throw new Exception("Need at least three existing known mod-key matches before mutation");
            byte[] updated = Enable(original, parsed, key);
            if (original.SequenceEqual(updated)) { Console.WriteLine("Already enabled; no write."); return 0; }
            _ = Parse(updated);
            string backupDir = Path.Combine(Root, ".cache", "cs1-local-preferences");
            Directory.CreateDirectory(backupDir);
            string backup = Path.Combine(backupDir, "userGameState." + Sha(original) + ".cgs");
            if (!File.Exists(backup)) using (var b = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { b.Write(original); b.Flush(true); }
            if (!File.ReadAllBytes(backup).SequenceEqual(original)) throw new Exception("Backup integrity failed");
            File.WriteAllText(backup + ".sha256", Sha(original) + "  " + Path.GetFileName(backup) + Environment.NewLine);
            // Exclusive handle denies new readers/writers until the last possible check. Windows Replace requires closing it.
            string temp = file + ".SC4Evolution." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(updated); output.Flush(true); }
                RefuseRunning();
                using (var check = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                { if (check.Length != original.Length || !SHA256.HashData(check).SequenceEqual(SHA256.HashData(original))) throw new Exception("Preferences changed since inspection; refusing"); }
                RefuseRunning();
                if (Sha(ReadBounded(file)) != Sha(original)) throw new Exception("Preferences changed before replacement");
                File.Replace(temp, file, null);
                if (!ReadBounded(file).SequenceEqual(updated)) throw new Exception("Replacement verification failed; exact original backup: " + backup);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Console.WriteLine("Enabled SC4Evolution only. Exact original backup: " + backup);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    static string ResolveGame()
    {
        using var config = JsonDocument.Parse(ReadBounded(Path.Combine(Root, "config", "local.paths.json")));
        string configured = config.RootElement.GetProperty("CitiesSkylines").GetString() ?? throw new Exception("CitiesSkylines path is missing");
        if (!Path.IsPathFullyQualified(configured)) throw new Exception("CitiesSkylines must be an absolute path");
        string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        string steamapps = Path.GetFullPath(Path.Combine(game, "..", ".."));
        if (!string.Equals(Directory.GetParent(game)?.Name, "common", StringComparison.OrdinalIgnoreCase)) throw new Exception("Expected a Steam steamapps/common install");
        string manifest = Utf8.GetString(ReadBounded(Path.Combine(steamapps, "appmanifest_255710.acf")));
        if (!Regex.IsMatch(manifest, "\\\"appid\\\"\\s+\\\"255710\\\"")) throw new Exception("Steam manifest does not identify Cities: Skylines (255710)");
        var directory = Regex.Match(manifest, "\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"");
        if (!directory.Success || !string.Equals(directory.Groups[1].Value, Path.GetFileName(game), StringComparison.OrdinalIgnoreCase)) throw new Exception("Steam installdir differs from configured game path");
        var exe = FileVersionInfo.GetVersionInfo(Path.Combine(game, "Cities.exe"));
        if (exe.ProductVersion != "5.6.7.15207619") throw new Exception("Unsupported Cities executable version; revalidate hash source first");
        if (Sha(ReadBounded(Path.Combine(game, "Cities_Data", "Managed", "mscorlib.dll"))) != "57e3a820a75c409fa1965a550d8f0d74eb4e1364d98afd0424a7f617c7b4487f") throw new Exception("Game mscorlib changed; revalidate selected String.GetHashCode first");
        return game;
    }
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static byte[] ReadBounded(string file) { using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); if (f.Length > MaximumBytes) throw new Exception("Preferences exceed 32 MiB"); var b = new byte[(int)f.Length]; f.ReadExactly(b); return b; }
    static void RefuseRunning() { if (Process.GetProcessesByName("Cities").Length != 0 || Process.GetProcessesByName("Cities_Skylines").Length != 0) throw new Exception("Cities is running; refusing preference mutation"); }
    static string Key(string path, int hash) => Path.GetFileNameWithoutExtension(path) + hash.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".enabled";
    static int GameHash(string s) { int h=0; unchecked { foreach (char c in s) h = h*31+c; } return h; }
    static int[] NativeHashes(string[] paths)
    {
        var psi = new ProcessStartInfo(Path.Combine(Game, "Mono", "bin", "mono.exe")) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
        psi.ArgumentList.Add(Path.Combine(Root, "tools", "CS1LocalPreferences", "bin", "NativeHash.exe"));
        foreach (string p in paths) psi.ArgumentList.Add(p);
        using var proc = Process.Start(psi) ?? throw new Exception("Cannot start bundled Mono");
        if (!proc.WaitForExit(10000)) { proc.Kill(); throw new Exception("Bundled Mono timed out"); }
        string output = proc.StandardOutput.ReadToEnd(); string error = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0) throw new Exception("Bundled Mono failed: " + error);
        var lines=output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != paths.Length) throw new Exception("Unexpected bundled Mono output");
        return lines.Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
    sealed record Parsed(Dictionary<string,int> Bools, int BoolCountOffset, int BoolEnd, int BoolCount);
    static Parsed Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new Exception("Preferences exceed 32 MiB");
        using var s = new MemoryStream(bytes,false); using var r = new BinaryReader(s,Utf8);
        if (!r.ReadBytes(4).SequenceEqual("CGSF"u8.ToArray()) || r.ReadUInt16()!=4) throw new Exception("Only CGSF version 4 is supported");
        Dictionary<string,int> bools = new(StringComparer.Ordinal); int bo=0,be=0,bc=0;
        for (int type=0;type<6;type++)
        {
            int countOffset=checked((int)s.Position), count=r.ReadInt32();
            if (count<0 || count>250000 || count>(s.Length-s.Position)/2) throw new Exception("Invalid dictionary count");
            HashSet<string> keys=new(StringComparer.Ordinal);
            if(type==1) { bo=countOffset; bc=count; }
            for(int i=0;i<count;i++)
            {
                string key=ReadString(r); if(!keys.Add(key)) throw new Exception("Duplicate dictionary key");
                int offset=checked((int)s.Position);
                switch(type) {
                    case 0: case 2: case 4: r.ReadInt32(); break;
                    case 1: byte b=r.ReadByte(); if(b>1) throw new Exception("Noncanonical boolean"); bools.Add(key,offset); break;
                    case 3: ReadString(r); break;
                    case 5: r.ReadInt64(); break;
                }
            }
            if(type==1) be=checked((int)s.Position);
        }
        if(s.Position!=s.Length) throw new Exception("Trailing bytes");
        return new Parsed(bools,bo,be,bc);
    }
    static string ReadString(BinaryReader r)
    {
        uint length=0; int shift=0;
        for(int i=0;i<5;i++) { byte b=r.ReadByte(); if(i==4 && b>7) throw new Exception("Invalid string length"); length|=(uint)(b&127)<<shift; if((b&128)==0) { if(i>0 && b==0) throw new Exception("Noncanonical string length"); if(length>1024*1024 || length>r.BaseStream.Length-r.BaseStream.Position) throw new Exception("String exceeds bounds"); byte[] data=r.ReadBytes((int)length); return Utf8.GetString(data); } shift+=7; }
        throw new Exception("Invalid string length");
    }
    static byte[] Enable(byte[] original, Parsed p, string key)
    {
        if (original.Length > MaximumBytes) throw new Exception("Preferences exceed 32 MiB");
        if(p.Bools.TryGetValue(key,out int offset)) { byte[] copy=(byte[])original.Clone(); copy[offset]=1; return copy; }
        using var entry=new MemoryStream(); using(var w=new BinaryWriter(entry,Utf8,true)) { w.Write(key); w.Write(true); }
        byte[] inserted=entry.ToArray(); int updatedLength=checked(original.Length+inserted.Length);
        if (updatedLength > MaximumBytes) throw new Exception("Updated preferences would exceed 32 MiB");
        byte[] result=new byte[updatedLength];
        original.AsSpan(0,p.BoolEnd).CopyTo(result); inserted.CopyTo(result,p.BoolEnd); original.AsSpan(p.BoolEnd).CopyTo(result.AsSpan(p.BoolEnd+inserted.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(p.BoolCountOffset,4),checked(p.BoolCount+1)); return result;
    }
    static void SelfTest()
    {
        using var stream=new MemoryStream(); using(var w=new BinaryWriter(stream,Utf8,true)) { w.Write("CGSF"u8); w.Write((ushort)4); for(int t=0;t<6;t++) { w.Write(1); w.Write("key"+t); switch(t) { case 0: case 4: w.Write(42); break; case 1: w.Write(false); break; case 2: w.Write(BitConverter.Int32BitsToSingle(unchecked((int)0x7FC12345))); break; case 3: w.Write("á𝄞"); break; case 5: w.Write(long.MaxValue); break; } } }
        byte[] b=stream.ToArray(); var p=Parse(b); byte[] u=Enable(b,p,"key1"); if(b.Zip(u).Count(x=>x.First!=x.Second)!=1 || u[p.Bools["key1"]]!=1) throw new Exception("Existing bool preservation failed");
        byte[] added=Enable(b,p,"SC4Evolution-test.enabled"); var ap=Parse(added); if(ap.BoolCount!=2 || !added.AsSpan(ap.BoolEnd).SequenceEqual(b.AsSpan(p.BoolEnd))) throw new Exception("Insert preservation failed");
        if(!Enable(added,ap,"SC4Evolution-test.enabled").SequenceEqual(added)) throw new Exception("Idempotence failed");
        int rejected=0; void Reject(byte[] bad) { try { Parse(bad); } catch { rejected++; return; } throw new Exception("Malformed file accepted"); }
        Reject(b.Concat(new byte[]{0}).ToArray()); Reject(b[..^1]); var version=(byte[])b.Clone(); version[4]=5; Reject(version);
        var badBool=(byte[])b.Clone(); badBool[p.Bools["key1"]]=2; Reject(badBool);
        var badUtf=(byte[])b.Clone(); badUtf[11]=255; Reject(badUtf);
        var badCount=(byte[])b.Clone(); BinaryPrimitives.WriteInt32LittleEndian(badCount.AsSpan(6),-1); Reject(badCount);
        // Add a duplicate bool entry with an incremented dictionary count.
        using var de=new MemoryStream(); using(var dw=new BinaryWriter(de,Utf8,true)) { dw.Write("key1"); dw.Write(true); } byte[] eb=de.ToArray(); byte[] duplicate=new byte[b.Length+eb.Length]; b.AsSpan(0,p.BoolEnd).CopyTo(duplicate); eb.CopyTo(duplicate,p.BoolEnd); b.AsSpan(p.BoolEnd).CopyTo(duplicate.AsSpan(p.BoolEnd+eb.Length)); BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(p.BoolCountOffset),2); Reject(duplicate);
        // Exactly 32 MiB, composed of bounded strings, remains readable and editable in place.
        using var boundaryStream=new MemoryStream();
        using(var w=new BinaryWriter(boundaryStream,Utf8,true))
        {
            w.Write("CGSF"u8); w.Write((ushort)4); w.Write(0);
            w.Write(1); w.Write("key1"); w.Write(false);
            w.Write(0); w.Write(32);
            string chunk=new('x',1024*1024-1);
            for(int i=0;i<31;i++) { w.Write("f"+i); w.Write(chunk); }
            w.Write("last");
            int remaining=checked(MaximumBytes-(int)boundaryStream.Position-8-3);
            w.Write(new string('x',remaining)); w.Write(0); w.Write(0);
        }
        byte[] boundary=boundaryStream.ToArray();
        if(boundary.Length!=MaximumBytes) throw new Exception("Boundary fixture size is incorrect");
        var bp=Parse(boundary); byte[] boundaryEnabled=Enable(boundary,bp,"key1"); _=Parse(boundaryEnabled);
        if(boundary.Zip(boundaryEnabled).Count(x=>x.First!=x.Second)!=1) throw new Exception("Boundary existing bool edit failed");
        byte[] boundaryDigest=SHA256.HashData(boundary); bool insertionRejected=false;
        try { Enable(boundary,bp,"SC4Evolution-test.enabled"); } catch(Exception e) when(e.Message=="Updated preferences would exceed 32 MiB") { insertionRejected=true; }
        if(!insertionRejected || !SHA256.HashData(boundary).SequenceEqual(boundaryDigest)) throw new Exception("Oversize insertion changed original or was accepted");
        Reject(boundary.Concat(new byte[]{0}).ToArray());
        Console.WriteLine("Boundary tests passed: exact 32 MiB existing edit, oversize insertion refused with unchanged original, oversize Parse rejected.");
        Console.WriteLine("Self-test passed: all six types, raw NaN bits, UTF8, one-byte edit, insertion, idempotence, " + rejected + " malformed cases.");
    }
}





