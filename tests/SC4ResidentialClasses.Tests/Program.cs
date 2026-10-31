using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SC4Evolution.Simulation;

internal static class Program
{
    private const int HeaderLength = 74;
    private static readonly Guid StableLedgerId = new Guid("87fc1b17-0c54-4eb4-bad5-3e2b887e8c13");
    private static readonly byte[] StablePolicyHash = MakePolicyHash();
    private static int _assertions;

    private static int Main()
    {
        try
        {
            TestIdentityAndLedgerValidation();
            TestImportSelectionAndStaleFingerprints();
            TestUpgradeRefreshBoundaries();
            TestDeterministicCodecRoundTripAndOpenStreams();
            TestRejectsEveryTruncationAndCorruption();
            TestRejectsMalformedFieldsAndOrdering();
            TestGenerationOverflowAndFailureAtomicity();
            TestPayloadAndRecordCountBounds();
            int waterAssertions = WaterProfileTests.Run();
            Console.WriteLine("PASS: " + _assertions + " ledger assertions across 8 scenarios; " +
                waterAssertions + " water-profile assertions across 5 scenarios.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void TestIdentityAndLedgerValidation()
    {
        ResidentialLotIdentity valid = Identity(1, 0, 1.25f, -2.5f, "Residencial/Étoile", 1, 0, 255, 255);
        Equal((ushort)1, valid.BuildingId, "valid identity preserves building ID");
        Equal(0u, valid.NativeBuildIndex, "native build index zero is valid");
        Equal(Bits(1.25f), valid.OriginXBits, "origin X preserves exact float bits");
        Equal(Bits(-2.5f), valid.OriginZBits, "origin Z preserves exact float bits");
        Equal("Residencial/Étoile", valid.PrefabName, "identity preserves UTF-8 prefab name");
        Equal((byte)255, valid.Width, "width 255 is valid");
        Equal((byte)255, valid.Length, "length 255 is valid");

        Throws<ArgumentOutOfRangeException>(delegate { Identity(0, 0, 0.0f, 0.0f, "Home", 1, 1, 1, 1); }, "building ID zero is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialLotIdentity(1, 0, unchecked((int)0x7F800000), 0, "Home", 1, 1, 1, 1); }, "infinite X bits are rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialLotIdentity(1, 0, 0, unchecked((int)0x7FC00000), "Home", 1, 1, 1, 1); }, "NaN Z bits are rejected");
        Throws<ArgumentException>(delegate { Identity(1, 0, 0.0f, 0.0f, "", 1, 1, 1, 1); }, "empty prefab name is rejected");
        Throws<ArgumentException>(delegate { Identity(1, 0, 0.0f, 0.0f, "\uD800", 1, 1, 1, 1); }, "unpaired surrogate prefab name is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { Identity(1, 0, 0.0f, 0.0f, new string('x', 1025), 1, 1, 1, 1); }, "prefab name over 1024 UTF-8 bytes is rejected");
        string exactByteLimit = new string('\u00E9', 512);
        Equal(1024, Encoding.UTF8.GetByteCount(Identity(1, 0, 0.0f, 0.0f, exactByteLimit, 1, 1, 1, 1).PrefabName), "1024-byte UTF-8 prefab name is accepted");
        Throws<ArgumentOutOfRangeException>(delegate { Identity(1, 0, 0.0f, 0.0f, "Home", -1, 1, 1, 1); }, "negative service is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { Identity(1, 0, 0.0f, 0.0f, "Home", 1, -1, 1, 1); }, "negative subservice is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { Identity(1, 0, 0.0f, 0.0f, "Home", 1, 1, 0, 1); }, "zero width is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { Identity(1, 0, 0.0f, 0.0f, "Home", 1, 1, 1, 0); }, "zero length is rejected");
        Throws<ArgumentException>(delegate { new ResidentialClassLedger(Guid.Empty, 1, StablePolicyHash); }, "empty ledger GUID is rejected");
        Throws<ArgumentException>(delegate { new ResidentialClassLedger(StableLedgerId, 1, new byte[31]); }, "short policy hash is rejected");

        byte[] callerHash = MakePolicyHash();
        ResidentialClassLedger ledger = new ResidentialClassLedger(StableLedgerId, 3, callerHash);
        callerHash[0] ^= 0xFF;
        Equal(StablePolicyHash[0], ledger.PolicyHash[0], "ledger clones the caller policy hash");
        byte[] returnedHash = ledger.PolicyHash;
        returnedHash[0] ^= 0xFF;
        Equal(StablePolicyHash[0], ledger.PolicyHash[0], "policy hash getter returns a clone");
        Equal(1UL, ledger.NextGeneration, "new ledger starts at generation one");
        Equal(0, ledger.Count, "new ledger starts empty");
    }

    private static void TestImportSelectionAndStaleFingerprints()
    {
        ResidentialClassLedger ledger = NewLedger();
        ResidentialLotIdentity first = Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-A", 1, 0, 2, 3);
        ResidentialLotIdentity second = Identity(7, 88, 2.0f, 4.0f, "Homes/Level1-B", 1, 1, 3, 2);
        ResidentialLotRecord selected = ledger.CommitCreated(first, ResidentialClass.High);
        Equal(1UL, selected.Generation, "selected creation receives first generation");
        Equal(ClassOrigin.Selected, selected.Origin, "controlled creation is marked selected");
        Equal(ResidentialClass.High, selected.DesignClass, "selected design class is stored");
        Equal(ResidentialClass.High, selected.OccupantClass, "v1 occupant class equals design class");
        Equal(2UL, ledger.NextGeneration, "commit advances next generation monotonically");

        ResidentialLotRecord imported = ledger.ImportExisting(second, ResidentialClass.Low);
        Equal(2UL, imported.Generation, "import receives a fresh generation");
        Equal(ClassOrigin.ImportedNative, imported.Origin, "native bootstrap import is labeled");
        ResidentialLotRecord importedAgain = ledger.ImportExisting(second, ResidentialClass.High);
        True(object.ReferenceEquals(imported, importedAgain), "exact import is idempotent and does not reclassify existing record");
        Equal(ResidentialClass.Low, importedAgain.OccupantClass, "idempotent import preserves original bootstrap class");

        ResidentialLotRecord found;
        True(ledger.TryGet(first, out found), "exact fingerprint lookup succeeds");
        True(object.ReferenceEquals(selected, found), "lookup returns the immutable stored record");
        ResidentialLotIdentity stale = Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-B", 1, 0, 2, 3);
        True(!ledger.TryGet(stale, out found), "same ID with a stale prefab fingerprint is unbound");
        True(found == null, "fingerprint miss sets the output record to null");
        AssertFingerprintMiss(ledger, Identity(41, 1, 12.5f, -8.0f, "Homes/Level1-A", 1, 0, 2, 3), "build index participates in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.75f, -8.0f, "Homes/Level1-A", 1, 0, 2, 3), "origin X bits participate in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.5f, -8.25f, "Homes/Level1-A", 1, 0, 2, 3), "origin Z bits participate in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-A", 2, 0, 2, 3), "service participates in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-A", 1, 1, 2, 3), "subservice participates in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-A", 1, 0, 3, 3), "width participates in identity");
        AssertFingerprintMiss(ledger, Identity(41, 0, 12.5f, -8.0f, "Homes/Level1-A", 1, 0, 2, 4), "length participates in identity");
        AssertFingerprintMiss(ledger, Identity(42, 0, 12.5f, -8.0f, "Homes/Level1-A", 1, 0, 2, 3), "building ID participates in identity");
        True(!ledger.TryGet(null, out found), "null identity is an unbound lookup");
        True(found == null, "null lookup sets the output record to null");

        ResidentialLotRecord replaced = ledger.ImportExisting(stale, ResidentialClass.Medium);
        Equal(3UL, replaced.Generation, "stale identity replacement receives a fresh generation");
        Equal(ClassOrigin.ImportedNative, replaced.Origin, "stale replacement is marked imported");
        True(!ledger.TryGet(first, out found), "replaced old fingerprint no longer resolves");
        ResidentialLotRecord recreated = ledger.CommitCreated(stale, ResidentialClass.High);
        Equal(4UL, recreated.Generation, "successful re-creation replaces the same ID with a fresh generation");
        Equal(ClassOrigin.Selected, recreated.Origin, "re-created record is selected");

        ResidentialLotRecord[] records = ledger.GetRecords();
        Equal((ushort)7, records[0].Identity.BuildingId, "record copy is sorted by building ID");
        Equal((ushort)41, records[1].Identity.BuildingId, "record copy contains the larger ID second");
        records[0] = null;
        True(ledger.TryGet(stale, out found), "mutating the returned array cannot alter ledger contents");
        True(!ledger.Remove(0), "removing ID zero is always false");
        True(ledger.Remove(7), "existing record is removed");
        True(!ledger.Remove(7), "removed record cannot be removed twice");
        Equal(1, ledger.Count, "remove updates count only for an existing nonzero ID");

        int oldCount = ledger.Count;
        ulong oldNext = ledger.NextGeneration;
        Throws<ArgumentOutOfRangeException>(delegate { ledger.CommitCreated(first, (ResidentialClass)4); }, "invalid class is rejected before commit");
        Throws<ArgumentOutOfRangeException>(delegate { ledger.ImportExisting(first, (ResidentialClass)0); }, "invalid import class is rejected before mutation");
        Equal(oldCount, ledger.Count, "invalid class leaves record count unchanged");
        Equal(oldNext, ledger.NextGeneration, "invalid class leaves generation unchanged");
    }

    private static void TestUpgradeRefreshBoundaries()
    {
        ResidentialClassLedger ledger = NewLedger();
        ResidentialLotIdentity before = Identity(18, 9, -1.0f, 3.5f, "Homes/Small", 1, 2, 2, 3);
        ResidentialLotRecord original = ledger.CommitCreated(before, ResidentialClass.Medium);
        ResidentialLotIdentity after = Identity(18, 9, -1.0f, 3.5f, "Homes/CornerUpgrade", 1, 7, 3, 2);
        True(ledger.RefreshUpgradeIdentity(before, after), "controlled upgrade may refresh prefab, subservice and footprint");
        ResidentialLotRecord refreshed;
        True(ledger.TryGet(after, out refreshed), "refreshed fingerprint is bound");
        Equal(original.Generation, refreshed.Generation, "upgrade preserves generation");
        Equal(original.DesignClass, refreshed.DesignClass, "upgrade preserves design class");
        Equal(original.OccupantClass, refreshed.OccupantClass, "upgrade preserves occupant class");
        Equal(original.Origin, refreshed.Origin, "upgrade preserves import/selection origin");
        True(!ledger.TryGet(before, out refreshed), "pre-upgrade fingerprint is no longer current");

        ResidentialLotIdentity staleBefore = Identity(18, 9, -1.0f, 3.5f, "Homes/Other", 1, 7, 3, 2);
        ResidentialLotIdentity attemptedAfter = Identity(18, 9, -1.0f, 3.5f, "Homes/ShouldNotApply", 1, 8, 4, 2);
        True(!ledger.RefreshUpgradeIdentity(staleBefore, attemptedAfter), "upgrade refresh requires an exact pre-call fingerprint");
        True(ledger.TryGet(after, out refreshed), "failed stale refresh leaves current record untouched");
        Equal("Homes/CornerUpgrade", refreshed.Identity.PrefabName, "failed stale refresh preserves current prefab name");

        ResidentialLotIdentity changedService = Identity(18, 9, -1.0f, 3.5f, "Homes/InvalidServiceChange", 2, 8, 4, 2);
        True(!ledger.RefreshUpgradeIdentity(after, changedService), "upgrade boundary refuses service changes");
        ResidentialLotIdentity changedBuildIndex = Identity(18, 10, -1.0f, 3.5f, "Homes/InvalidBuildIndex", 1, 8, 4, 2);
        True(!ledger.RefreshUpgradeIdentity(after, changedBuildIndex), "upgrade boundary refuses build-index changes");
        ResidentialLotIdentity moved = Identity(18, 9, -1.0f, 3.75f, "Homes/InvalidMove", 1, 8, 4, 2);
        True(!ledger.RefreshUpgradeIdentity(after, moved), "upgrade boundary refuses creation-position changes");
        True(ledger.TryGet(after, out refreshed), "all invalid upgrade transitions leave prior binding intact");
        Equal("Homes/CornerUpgrade", refreshed.Identity.PrefabName, "identity is unchanged after rejected upgrades");
    }

    private static void TestDeterministicCodecRoundTripAndOpenStreams()
    {
        ResidentialClassLedger ledger = NewLedger();
        ledger.CommitCreated(Identity(90, 0, 9.0f, 1.0f, "Homes/Selected", 1, 3, 1, 2), ResidentialClass.High);
        ledger.ImportExisting(Identity(3, 77, 0.5f, -0.0f, "Homes/Étage", 1, 0, 4, 5), ResidentialClass.Low);
        ledger.CommitCreated(Identity(24, 5, -4.0f, 12.0f, "Homes/多言語", 1, 4, 2, 2), ResidentialClass.Medium);

        byte[] first = Serialize(ledger);
        byte[] repeated = Serialize(ledger);
        Equal(0xCBF43926u, Crc32(Encoding.ASCII.GetBytes("123456789"), 0, 9), "checksum implementation matches CRC-32/ISO-HDLC standard vector");
        BytesEqual(Encoding.ASCII.GetBytes("SC4RCLS1"), Slice(first, 0, 8), "payload begins with exact signature bytes");
        Equal((byte)1, first[8], "version 1 is encoded little-endian in the header");
        Equal((byte)0, first[9], "version high byte is zero");
        BytesEqual(StableLedgerId.ToByteArray(), Slice(first, 10, 16), "ledger GUID uses Guid.ToByteArray byte order");
        Equal((byte)3, first[26], "policy version starts at the specified header offset");
        BytesEqual(StablePolicyHash, Slice(first, 30, 32), "policy hash follows policy version");
        Equal(4UL, ReadUInt64(first, 62), "next generation is encoded little-endian at the specified offset");
        Equal((uint)3, ReadUInt32(first, 70), "record count is encoded little-endian at the specified offset");
        Equal((ushort)3, ReadUInt16(first, HeaderLength), "first record is sorted building ID at the exact record offset");
        BytesEqual(first, repeated, "same ledger serializes deterministically");
        True(first.Length <= 16 * 1024 * 1024, "serialized record is within project payload bound");
        using (MemoryStream output = new MemoryStream())
        {
            ResidentialClassLedgerCodec.Write(output, ledger);
            True(output.CanWrite, "codec writer leaves caller stream open");
            BytesEqual(first, output.ToArray(), "codec output matches deterministic serialization");
        }

        ResidentialClassLedger loaded;
        using (MemoryStream input = new MemoryStream(first, false))
        {
            loaded = ResidentialClassLedgerCodec.Read(input);
            True(input.CanRead, "codec reader leaves caller stream open");
            Equal(-1, input.ReadByte(), "successful read consumes the complete standalone payload");
        }
        Equal(StableLedgerId, loaded.LedgerId, "roundtrip preserves ledger GUID byte order");
        Equal((uint)3, loaded.PolicyVersion, "roundtrip preserves policy version");
        BytesEqual(StablePolicyHash, loaded.PolicyHash, "roundtrip preserves policy hash");
        Equal(4UL, loaded.NextGeneration, "roundtrip preserves next generation");
        Equal(3, loaded.Count, "roundtrip preserves record count");
        Equal((ushort)3, loaded.GetRecords()[0].Identity.BuildingId, "codec restores records in ascending ID order");
        Equal((ushort)24, loaded.GetRecords()[1].Identity.BuildingId, "codec restores middle sorted ID");
        Equal((ushort)90, loaded.GetRecords()[2].Identity.BuildingId, "codec restores final sorted ID");
        BytesEqual(first, Serialize(loaded), "roundtrip is byte-stable");

        ResidentialLotRecord[] before = ledger.GetRecords();
        ResidentialLotRecord[] after = loaded.GetRecords();
        Equal(before.Length, after.Length, "record count stable through reload");
        int index;
        for (index = 0; index < before.Length; index++)
            RecordEqual(before[index], after[index], "roundtrip record " + index.ToString());
    }

    private static void TestRejectsEveryTruncationAndCorruption()
    {
        byte[] valid = Serialize(LedgerWithTwoRecords());
        int cut;
        for (cut = 0; cut < valid.Length; cut++)
        {
            byte[] prefix = new byte[cut];
            Array.Copy(valid, prefix, cut);
            Throws<InvalidDataException>(delegate { Read(prefix); }, "truncation at byte " + cut.ToString() + " is rejected");
        }

        byte[] badChecksum = (byte[])valid.Clone();
        badChecksum[badChecksum.Length - 1] ^= 0x80;
        Throws<InvalidDataException>(delegate { Read(badChecksum); }, "checksum corruption is rejected");

        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0x01;
        Rechecksum(badMagic);
        Throws<InvalidDataException>(delegate { Read(badMagic); }, "unknown magic is rejected after valid CRC");

        byte[] unknownVersion = (byte[])valid.Clone();
        PutUInt16(unknownVersion, 8, 2);
        Rechecksum(unknownVersion);
        Throws<InvalidDataException>(delegate { Read(unknownVersion); }, "unknown version is rejected");

        byte[] trailing = AppendBeforeChecksum(valid, new byte[] { 0x7F });
        Throws<InvalidDataException>(delegate { Read(trailing); }, "valid-CRC trailing payload byte is rejected");

        byte[] emptyGuid = (byte[])valid.Clone();
        Array.Clear(emptyGuid, 10, 16);
        Rechecksum(emptyGuid);
        Throws<InvalidDataException>(delegate { Read(emptyGuid); }, "empty serialized GUID is rejected");
    }

    private static void TestRejectsMalformedFieldsAndOrdering()
    {
        byte[] valid = Serialize(LedgerWithTwoRecords());
        int firstRecord = HeaderLength;
        int firstRecordLength = RecordLength(valid, firstRecord);
        int secondRecord = firstRecord + firstRecordLength;

        byte[] duplicateId = (byte[])valid.Clone();
        PutUInt16(duplicateId, secondRecord, ReadUInt16(duplicateId, firstRecord));
        Rechecksum(duplicateId);
        Throws<InvalidDataException>(delegate { Read(duplicateId); }, "duplicate building IDs are rejected");

        byte[] unsorted = (byte[])valid.Clone();
        PutUInt16(unsorted, firstRecord, 9);
        PutUInt16(unsorted, secondRecord, 2);
        Rechecksum(unsorted);
        Throws<InvalidDataException>(delegate { Read(unsorted); }, "unsorted records are rejected");

        byte[] duplicateGeneration = (byte[])valid.Clone();
        PutUInt64(duplicateGeneration, secondRecord + 6, ReadUInt64(duplicateGeneration, firstRecord + 6));
        Rechecksum(duplicateGeneration);
        Throws<InvalidDataException>(delegate { Read(duplicateGeneration); }, "duplicate generations are rejected");

        byte[] zeroGeneration = (byte[])valid.Clone();
        PutUInt64(zeroGeneration, firstRecord + 6, 0);
        Rechecksum(zeroGeneration);
        Throws<InvalidDataException>(delegate { Read(zeroGeneration); }, "generation zero is rejected");

        byte[] invalidOrigin = (byte[])valid.Clone();
        invalidOrigin[firstRecord + 14] = 0;
        Rechecksum(invalidOrigin);
        Throws<InvalidDataException>(delegate { Read(invalidOrigin); }, "unknown origin enum is rejected");

        byte[] invalidClass = (byte[])valid.Clone();
        invalidClass[firstRecord + 15] = 4;
        Rechecksum(invalidClass);
        Throws<InvalidDataException>(delegate { Read(invalidClass); }, "unknown class enum is rejected");

        byte[] unequalClasses = (byte[])valid.Clone();
        unequalClasses[firstRecord + 16] = (byte)ResidentialClass.High;
        Rechecksum(unequalClasses);
        Throws<InvalidDataException>(delegate { Read(unequalClasses); }, "version 1 unequal classes are rejected");

        byte[] invalidUtf8 = (byte[])valid.Clone();
        int firstNameOffset = firstRecord + 37;
        invalidUtf8[firstNameOffset] = 0xC3;
        Rechecksum(invalidUtf8);
        Throws<InvalidDataException>(delegate { Read(invalidUtf8); }, "invalid UTF-8 is rejected rather than replacement-decoded");

        byte[] infiniteX = (byte[])valid.Clone();
        PutInt32(infiniteX, firstRecord + 17, unchecked((int)0x7F800000));
        Rechecksum(infiniteX);
        Throws<InvalidDataException>(delegate { Read(infiniteX); }, "non-finite serialized creation coordinate is rejected");

        byte[] negativeService = (byte[])valid.Clone();
        PutInt32(negativeService, firstRecord + 25, -1);
        Rechecksum(negativeService);
        Throws<InvalidDataException>(delegate { Read(negativeService); }, "negative serialized service is rejected");

        byte[] zeroWidth = (byte[])valid.Clone();
        zeroWidth[firstRecord + 33] = 0;
        Rechecksum(zeroWidth);
        Throws<InvalidDataException>(delegate { Read(zeroWidth); }, "zero serialized width is rejected");

        byte[] nextNotAfterRecords = (byte[])valid.Clone();
        PutUInt64(nextNotAfterRecords, 62, 2);
        Rechecksum(nextNotAfterRecords);
        Throws<InvalidDataException>(delegate { Read(nextNotAfterRecords); }, "next generation must exceed all records");

        byte[] zeroBuildingId = (byte[])valid.Clone();
        PutUInt16(zeroBuildingId, firstRecord, 0);
        Rechecksum(zeroBuildingId);
        Throws<InvalidDataException>(delegate { Read(zeroBuildingId); }, "serialized building ID zero is rejected");
    }

    private static void TestGenerationOverflowAndFailureAtomicity()
    {
        ResidentialClassLedger stable = LedgerWithTwoRecords();
        ResidentialLotIdentity stableIdentity = stable.GetRecords()[0].Identity;
        ResidentialLotRecord stableRecord;
        True(stable.TryGet(stableIdentity, out stableRecord), "valid ledger is ready before malformed load");
        ulong stableNext = stable.NextGeneration;
        int stableCount = stable.Count;

        byte[] corrupt = Serialize(stable);
        corrupt[corrupt.Length - 4] ^= 0x10;
        Throws<InvalidDataException>(delegate { Read(corrupt); }, "bad reload fails as a single operation");
        True(stable.TryGet(stableIdentity, out stableRecord), "failed parse cannot mutate previously valid ledger binding");
        Equal(stableNext, stable.NextGeneration, "failed parse cannot reset the previous next generation");
        Equal(stableCount, stable.Count, "failed parse cannot partially remove prior records");

        byte[] exhaustedPayload = Serialize(NewLedger());
        PutUInt64(exhaustedPayload, 62, ulong.MaxValue);
        Rechecksum(exhaustedPayload);
        ResidentialClassLedger exhausted = Read(exhaustedPayload);
        Equal(ulong.MaxValue, exhausted.NextGeneration, "valid empty ledger may retain the terminal next-generation value");
        Throws<InvalidOperationException>(delegate { exhausted.CommitCreated(Identity(5, 0, 0.0f, 0.0f, "Home", 1, 1, 1, 1), ResidentialClass.Low); }, "generation exhaustion is rejected instead of wrapping");
        Equal(0, exhausted.Count, "generation overflow rejection leaves ledger empty");
        Equal(ulong.MaxValue, exhausted.NextGeneration, "generation overflow rejection leaves next generation unchanged");

        byte[] lastAssignablePayload = Serialize(NewLedger());
        PutUInt64(lastAssignablePayload, 62, ulong.MaxValue - 1);
        Rechecksum(lastAssignablePayload);
        ResidentialClassLedger lastAssignable = Read(lastAssignablePayload);
        ResidentialLotRecord finalRecord = lastAssignable.CommitCreated(
            Identity(6, 0, 0.0f, 0.0f, "Home", 1, 1, 1, 1), ResidentialClass.Medium);
        Equal(ulong.MaxValue - 1, finalRecord.Generation, "last representable record generation is assignable");
        Equal(ulong.MaxValue, lastAssignable.NextGeneration, "last assignable generation advances to terminal sentinel");
        Throws<InvalidOperationException>(delegate { lastAssignable.CommitCreated(Identity(7, 0, 0.0f, 0.0f, "Home", 1, 1, 1, 1), ResidentialClass.Low); }, "generation cannot wrap after terminal sentinel");
        Equal(1, lastAssignable.Count, "post-boundary overflow rejection preserves existing record");

        byte[] maximumStoredGeneration = Serialize(LedgerWithTwoRecords());
        PutUInt64(maximumStoredGeneration, 62, ulong.MaxValue);
        PutUInt64(maximumStoredGeneration, HeaderLength + 6, ulong.MaxValue);
        Rechecksum(maximumStoredGeneration);
        Throws<InvalidDataException>(delegate { Read(maximumStoredGeneration); }, "no representable next generation after max stored generation is rejected");
    }

    private static void TestPayloadAndRecordCountBounds()
    {
        byte[] exactMaximum = MaximumPayloadFixture();
        Equal(16 * 1024 * 1024, exactMaximum.Length, "maximum payload fixture is exactly 16 MiB including CRC");
        ResidentialClassLedger maximumLedger = Read(exactMaximum);
        Equal(65535, maximumLedger.Count, "maximum legal record count is accepted");
        Equal(65536UL, maximumLedger.NextGeneration, "maximum record fixture preserves next generation ordering");
        Equal(16 * 1024 * 1024, Serialize(maximumLedger).Length, "writer accepts and bounds the exact 16 MiB payload limit");

        byte[] valid = Serialize(NewLedger());
        byte[] tooManyRecords = (byte[])valid.Clone();
        PutUInt32(tooManyRecords, 70, 65536);
        Rechecksum(tooManyRecords);
        Throws<InvalidDataException>(delegate { Read(tooManyRecords); }, "record count above 65535 is rejected before allocation");

        byte[] oversized = new byte[16 * 1024 * 1024 + 1];
        Throws<InvalidDataException>(delegate { Read(oversized); }, "payload above 16 MiB is rejected while reading");

        ResidentialClassLedger oversizedLedger = NewLedger();
        string largeName = new string('n', 1024);
        int id;
        for (id = 1; id <= 16000; id++)
            oversizedLedger.CommitCreated(Identity((ushort)id, (uint)id, 0.0f, 0.0f,
                largeName, 1, 0, 1, 1), ResidentialClass.Low);
        using (MemoryStream untouchedOutput = new MemoryStream())
        {
            Throws<InvalidDataException>(delegate { ResidentialClassLedgerCodec.Write(untouchedOutput, oversizedLedger); }, "writer rejects a ledger exceeding the serialized payload bound");
            Equal(0L, untouchedOutput.Length, "size rejection occurs before writing partial ledger bytes");
        }
        Equal(16000, oversizedLedger.Count, "oversize-save rejection leaves in-memory records intact");

        byte[] exactMaximumName = Serialize(SingleRecordLedger(new string('x', 1024)));
        Equal(1024, Read(exactMaximumName).GetRecords()[0].Identity.PrefabName.Length, "codec accepts prefab name at exact byte limit");

        MemoryStream readOnly = new MemoryStream(new byte[0], false);
        Throws<ArgumentException>(delegate { ResidentialClassLedgerCodec.Write(readOnly, NewLedger()); }, "writer rejects a nonwritable stream");
        MemoryStream writeOnly = new MemoryStream();
        writeOnly.Dispose();
        Throws<ArgumentException>(delegate { ResidentialClassLedgerCodec.Read(writeOnly); }, "reader rejects a nonreadable stream");
    }

    private static byte[] MaximumPayloadFixture()
    {
        const int maximumBytes = 16 * 1024 * 1024;
        const int recordCount = 65535;
        long additionalNameBytes = maximumBytes - 78L - recordCount * 38L;
        using (MemoryStream body = new MemoryStream(maximumBytes))
        {
            BinaryWriter writer = new BinaryWriter(body, Encoding.UTF8);
            writer.Write(Encoding.ASCII.GetBytes("SC4RCLS1"));
            writer.Write((ushort)1);
            writer.Write(StableLedgerId.ToByteArray());
            writer.Write((uint)3);
            writer.Write(StablePolicyHash);
            writer.Write((ulong)recordCount + 1UL);
            writer.Write((uint)recordCount);
            int id;
            for (id = 1; id <= recordCount; id++)
            {
                int extra = (int)Math.Min((long)1023, additionalNameBytes);
                ushort nameLength = (ushort)(1 + extra);
                additionalNameBytes -= extra;
                writer.Write((ushort)id);
                writer.Write((uint)0);
                writer.Write((ulong)id);
                writer.Write((byte)ClassOrigin.ImportedNative);
                writer.Write((byte)ResidentialClass.Low);
                writer.Write((byte)ResidentialClass.Low);
                writer.Write(0);
                writer.Write(0);
                writer.Write(1);
                writer.Write(0);
                writer.Write((byte)1);
                writer.Write((byte)1);
                writer.Write(nameLength);
                byte[] name = new byte[nameLength];
                int character;
                for (character = 0; character < name.Length; character++) name[character] = (byte)'x';
                writer.Write(name);
            }
            Equal(0L, additionalNameBytes, "exact-size fixture distributes all bounded name bytes");
            writer.Flush();
            byte[] bytesBeforeChecksum = body.ToArray();
            using (MemoryStream complete = new MemoryStream(maximumBytes))
            {
                complete.Write(bytesBeforeChecksum, 0, bytesBeforeChecksum.Length);
                BinaryWriter checksumWriter = new BinaryWriter(complete, Encoding.UTF8);
                checksumWriter.Write(Crc32(bytesBeforeChecksum, 0, bytesBeforeChecksum.Length));
                checksumWriter.Flush();
                return complete.ToArray();
            }
        }
    }

    private static ResidentialClassLedger NewLedger()
    {
        return new ResidentialClassLedger(StableLedgerId, 3, StablePolicyHash);
    }

    private static ResidentialClassLedger LedgerWithTwoRecords()
    {
        ResidentialClassLedger ledger = NewLedger();
        ledger.CommitCreated(Identity(9, 20, 4.0f, 8.0f, "Homes/Nine", 1, 0, 2, 2), ResidentialClass.High);
        ledger.ImportExisting(Identity(2, 10, -4.0f, 8.0f, "Homes/Two", 1, 1, 3, 1), ResidentialClass.Low);
        return ledger;
    }

    private static ResidentialClassLedger SingleRecordLedger(string name)
    {
        ResidentialClassLedger ledger = NewLedger();
        ledger.CommitCreated(Identity(1, 1, 0.0f, 0.0f, name, 1, 0, 1, 1), ResidentialClass.Low);
        return ledger;
    }

    private static ResidentialLotIdentity Identity(ushort id, uint buildIndex, float x, float z,
        string prefab, int service, int subService, byte width, byte length)
    {
        return new ResidentialLotIdentity(id, buildIndex, Bits(x), Bits(z), prefab,
            service, subService, width, length);
    }

    private static int Bits(float value)
    {
        return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
    }

    private static byte[] MakePolicyHash()
    {
        byte[] hash = new byte[32];
        int index;
        for (index = 0; index < hash.Length; index++) hash[index] = (byte)(index * 7 + 3);
        return hash;
    }

    private static byte[] Serialize(ResidentialClassLedger ledger)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            ResidentialClassLedgerCodec.Write(stream, ledger);
            return stream.ToArray();
        }
    }

    private static void AssertFingerprintMiss(ResidentialClassLedger ledger,
        ResidentialLotIdentity identity, string message)
    {
        ResidentialLotRecord record;
        True(!ledger.TryGet(identity, out record), message);
        True(record == null, message + " returns null record");
    }

    private static byte[] Slice(byte[] bytes, int offset, int count)
    {
        byte[] result = new byte[count];
        Array.Copy(bytes, offset, result, 0, count);
        return result;
    }

    private static ResidentialClassLedger Read(byte[] payload)
    {
        using (MemoryStream stream = new MemoryStream(payload, false))
            return ResidentialClassLedgerCodec.Read(stream);
    }

    private static int RecordLength(byte[] payload, int offset)
    {
        ushort nameLength = ReadUInt16(payload, offset + 35);
        return 37 + nameLength;
    }

    private static byte[] AppendBeforeChecksum(byte[] payload, byte[] suffix)
    {
        byte[] result = new byte[payload.Length + suffix.Length];
        Array.Copy(payload, 0, result, 0, payload.Length - 4);
        Array.Copy(suffix, 0, result, payload.Length - 4, suffix.Length);
        Array.Copy(payload, payload.Length - 4, result, payload.Length - 4 + suffix.Length, 4);
        Rechecksum(result);
        return result;
    }

    private static void Rechecksum(byte[] payload)
    {
        PutUInt32(payload, payload.Length - 4, Crc32(payload, 0, payload.Length - 4));
    }

    private static void PutUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static void PutUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static void PutInt32(byte[] bytes, int offset, int value)
    {
        PutUInt32(bytes, offset, unchecked((uint)value));
    }

    private static void PutUInt64(byte[] bytes, int offset, ulong value)
    {
        PutUInt32(bytes, offset, (uint)value);
        PutUInt32(bytes, offset + 4, (uint)(value >> 32));
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        return (ulong)bytes[offset] | ((ulong)bytes[offset + 1] << 8) |
            ((ulong)bytes[offset + 2] << 16) | ((ulong)bytes[offset + 3] << 24) |
            ((ulong)bytes[offset + 4] << 32) | ((ulong)bytes[offset + 5] << 40) |
            ((ulong)bytes[offset + 6] << 48) | ((ulong)bytes[offset + 7] << 56);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) |
            ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
    }

    private static uint Crc32(byte[] bytes, int offset, int count)
    {
        uint crc = uint.MaxValue;
        int index;
        for (index = offset; index < offset + count; index++)
        {
            crc ^= bytes[index];
            int bit;
            for (bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1u) == 0u ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static void RecordEqual(ResidentialLotRecord expected, ResidentialLotRecord actual, string message)
    {
        Equal(expected.Identity.BuildingId, actual.Identity.BuildingId, message + " building ID");
        Equal(expected.Identity.NativeBuildIndex, actual.Identity.NativeBuildIndex, message + " build index");
        Equal(expected.Identity.OriginXBits, actual.Identity.OriginXBits, message + " origin X bits");
        Equal(expected.Identity.OriginZBits, actual.Identity.OriginZBits, message + " origin Z bits");
        Equal(expected.Identity.PrefabName, actual.Identity.PrefabName, message + " prefab name");
        Equal(expected.Identity.Service, actual.Identity.Service, message + " service");
        Equal(expected.Identity.SubService, actual.Identity.SubService, message + " subservice");
        Equal(expected.Identity.Width, actual.Identity.Width, message + " width");
        Equal(expected.Identity.Length, actual.Identity.Length, message + " length");
        Equal(expected.Generation, actual.Generation, message + " generation");
        Equal(expected.DesignClass, actual.DesignClass, message + " design class");
        Equal(expected.OccupantClass, actual.OccupantClass, message + " occupant class");
        Equal(expected.Origin, actual.Origin, message + " origin");
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string message)
    {
        Equal(expected.Length, actual.Length, message + " length");
        int index;
        for (index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
                throw new Exception(message + ": byte difference at offset " + index.ToString() + ".");
            _assertions++;
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!object.Equals(expected, actual))
            throw new Exception(message + ": expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message + ": condition was false.");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        catch (Exception error) { throw new Exception(message + ": expected " + typeof(T).Name + ", got " + error.GetType().Name + ".", error); }
        throw new Exception(message + ": expected " + typeof(T).Name + " but no exception was thrown.");
    }
}
