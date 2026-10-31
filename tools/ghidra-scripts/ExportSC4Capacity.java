// Bounded static export from the existing, hash-verified SC4 Steam Ghidra project.
// This script reports only target addresses, instruction counts, export statuses, and failures.
// @category SC4Evolution
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.OutputStreamWriter;
import java.io.BufferedWriter;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

public class ExportSC4Capacity extends GhidraScript {
    private static final String EXPECTED_SHA256 = "f9b059d29940d1a28c89e6d6a7ab147e9762cd03134cdbaf02ba0e0142e1a176";
    @Override
    protected void run() throws Exception {
        String[] arguments = getScriptArgs();
        if (arguments.length != 2) throw new IllegalArgumentException("Pass the SC4Evolution root and mode (functions or helpers).");
        Path root = new File(arguments[0]).getCanonicalFile().toPath();
        if (!Files.isRegularFile(root.resolve("SC4Evolution.slnx"))) throw new IllegalArgumentException("Invalid project root.");
        String mode = arguments[1];
        final long[] targets;
        final String[] names;
        if (!"capacity".equals(mode)) throw new IllegalArgumentException("Expected capacity mode.");
        targets = new long[] {0x0070B440L,0x00695920L,0x00695980L,0x006958E0L,0x00695910L,0x006C3100L,0x006A2C80L,0x006A9470L,0x006C1940L,0x006C05A0L,0x006C0640L,0x00696130L,0x006959E0L,0x0069B910L};
        names = new String[] {"TractBuild","ClaimConstructionResources","ReleaseConstructionResources","GetFreeConstructionResources","GetTotalConstructionResources","LotSetState","DemandUpdateCapacity","DemandDoMessage","LotSetPopulation","LotGetCapacity","LotGetAvailableCapacity","BDSMonthly","BDSReadTuning","LoadBuildingProperties"};
        String actualSha = currentProgram.getExecutableSHA256();
        if (!EXPECTED_SHA256.equalsIgnoreCase(actualSha)) {
            throw new IllegalStateException("Input SHA-256 mismatch: expected " + EXPECTED_SHA256 + ", got " + actualSha);
        }
        if (!currentProgram.getImageBase().toString().equals("00400000")) {
            throw new IllegalStateException("Input image base mismatch: " + currentProgram.getImageBase());
        }

        Path output = root.resolve("analysis/generated/sc4-growth-capacity-native/exports");
        Files.createDirectories(output);
        String[] outputNames = new String[1 + names.length * 2];
        outputNames[0] = "manifest.txt";
        for (int i = 0; i < names.length; i++) {
            outputNames[1 + i * 2] = names[i] + ".asm.txt";
            outputNames[2 + i * 2] = names[i] + ".c.txt";
        }
        for (String name : outputNames) {
            if (Files.exists(output.resolve(name))) throw new IOException("Refusing to overwrite existing output: " + output.resolve(name));
        }

        DecompInterface decompiler = new DecompInterface();
        if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException("Could not open decompiler.");
        try (BufferedWriter manifest = createNew(output.resolve("manifest.txt"))) {
            manifest.write("Input=" + currentProgram.getName()); manifest.newLine();
            manifest.write("SHA256=" + actualSha); manifest.newLine();
            manifest.write("ImageBase=" + currentProgram.getImageBase()); manifest.newLine();
            manifest.write("Language=" + currentProgram.getLanguageID()); manifest.newLine();
            manifest.write("ProgramVersion=" + currentProgram.getExecutableFormat()); manifest.newLine();
            manifest.write("Mode=" + mode); manifest.newLine();

            for (int index = 0; index < targets.length; index++) {
                monitor.checkCancelled();
                String name = names[index];
                Address address = toAddr(targets[index]);
                manifest.write("\nTarget=" + name + " address=" + address); manifest.newLine();
                if (!currentProgram.getMemory().contains(address) || !currentProgram.getMemory().getBlock(address).isExecute()) {
                    manifest.write("Status=absent-or-non-executable"); manifest.newLine();
                    manifest.flush();
                    continue;
                }
                byte[] prefix = new byte[16];
                int bytesRead = currentProgram.getMemory().getBytes(address, prefix);
                StringBuilder hex = new StringBuilder();
                for (int i = 0; i < bytesRead; i++) hex.append(String.format("%02X ", prefix[i] & 255));
                manifest.write("ExecutableMemory=true prefixBytes=" + bytesRead + " prefix16=" + hex.toString().trim()); manifest.newLine();
                disassemble(address);
                Function function = getFunctionAt(address);
                if (function == null) function = createFunction(address, "candidate_" + name);
                if (function == null) {
                    manifest.write("FunctionStatus=creation-failed"); manifest.newLine();
                    manifest.flush();
                    continue;
                }

                int count = 0;
                boolean truncated = false;
                try (BufferedWriter asm = createNew(output.resolve(name + ".asm.txt"))) {
                    InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                    while (instructions.hasNext()) {
                        if (count >= 4096) { truncated = true; break; }
                        monitor.checkCancelled();
                        Instruction instruction = instructions.next();
                        asm.write(instruction.getAddress() + "  " + instruction.toString());
                        asm.newLine();
                        count++;
                    }
                }
                manifest.write("FunctionStatus=available name=" + function.getName()); manifest.newLine();
                manifest.write("Function=" + function.getName() + " body=" + function.getBody()); manifest.newLine();
                manifest.write("AssemblyStatus=" + (truncated ? "truncated-at-limit" : "complete") + " instructionCount=" + count); manifest.newLine();

                DecompileResults result = decompiler.decompileFunction(function, 15, monitor);
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    try (BufferedWriter pseudocode = createNew(output.resolve(name + ".c.txt"))) {
                        pseudocode.write(result.getDecompiledFunction().getC());
                    }
                    manifest.write("PseudocodeStatus=complete"); manifest.newLine();
                } else {
                    manifest.write("PseudocodeStatus=failed error=" + result.getErrorMessage()); manifest.newLine();
                }
                manifest.flush();
                println("Exported " + name + " at " + address);
            }
        } finally {
            decompiler.dispose();
        }
    }

    private static BufferedWriter createNew(Path path) throws IOException {
        return new BufferedWriter(new OutputStreamWriter(
            Files.newOutputStream(path, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE), StandardCharsets.UTF_8));
    }
}
