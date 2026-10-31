// Bounded static inspection of the user's installed SC4 executable.
// Candidate names/addresses reference tloriato/OpenSC4's pinned Windows table.
// The output is local evidence, not a claim of Steam/GOG equivalence.
// @category SC4Evolution
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

public class ExportSC4Demand extends GhidraScript {
    private static final long[] TARGETS = {0x005d4c40L, 0x005d4cf0L, 0x005d4e60L, 0x006a3dd0L, 0x006a4e70L, 0x006a6070L};
    private static final String[] NAMES = {"QuerySupplyValue", "EndOfCycle", "CalculateActiveDemand", "GetNeutralTaxRate", "DoDemandIndexLinks", "UpdateTaxAndEconomyModifiers"};

    @Override protected void run() throws Exception {
        String[] arguments = getScriptArgs();
        if (arguments.length != 1) throw new IllegalArgumentException("Pass the SC4Evolution root.");
        Path root = new File(arguments[0]).getCanonicalFile().toPath();
        if (!Files.isRegularFile(root.resolve("SC4Evolution.slnx"))) throw new IllegalArgumentException("Invalid project root.");
        Path output = root.resolve("analysis/generated/sc4-demand-native");
        Files.createDirectories(output);
        DecompInterface decompiler = new DecompInterface();
        if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException("Could not open decompiler.");
        try (PrintWriter summary = new PrintWriter(Files.newBufferedWriter(output.resolve("summary.txt"), StandardCharsets.UTF_8))) {
            summary.println("Static candidate analysis. No executable was run or modified.");
            summary.println("Program=" + currentProgram.getName());
            summary.println("SHA256=" + currentProgram.getExecutableSHA256());
            summary.println("Language=" + currentProgram.getLanguageID());
            summary.println("ImageBase=" + currentProgram.getImageBase());
            for (int index = 0; index < TARGETS.length; index++) {
                monitor.checkCancelled();
                Address address = toAddr(TARGETS[index]);
                if (!currentProgram.getMemory().contains(address) || !currentProgram.getMemory().getBlock(address).isExecute()) {
                    summary.println(NAMES[index] + " candidate is outside executable memory: " + address); continue;
                }
                byte[] prefix = new byte[16];
                currentProgram.getMemory().getBytes(address, prefix);
                StringBuilder hex = new StringBuilder();
                for (byte b : prefix) hex.append(String.format("%02X ", b & 255));
                summary.println(NAMES[index] + " @" + address + " prefix=" + hex);
                if (index == 0) summary.println("QuerySupplySignatureD94114C3=" +
                    ((prefix[0] & 255) == 0xD9 && (prefix[1] & 255) == 0x41 && (prefix[2] & 255) == 0x14 && (prefix[3] & 255) == 0xC3));
                disassemble(address);
                Function function = getFunctionAt(address);
                if (function == null) function = createFunction(address, "candidate_" + NAMES[index]);
                if (function == null) { summary.println("Function creation failed."); continue; }
                try (PrintWriter listing = new PrintWriter(Files.newBufferedWriter(output.resolve(NAMES[index] + ".asm.txt"), StandardCharsets.UTF_8))) {
                    InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                    int count = 0;
                    while (instructions.hasNext() && count++ < 4096) {
                        Instruction instruction = instructions.next();
                        listing.println(instruction.getAddress() + "  " + instruction.toString());
                    }
                    summary.println("Instructions=" + count + "; body=" + function.getBody());
                }
                DecompileResults result = decompiler.decompileFunction(function, 15, monitor);
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    Files.writeString(output.resolve(NAMES[index] + ".c.txt"), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                    summary.println("Decompiler=completed");
                } else summary.println("Decompiler=" + result.getErrorMessage());
                summary.flush();
                println("Exported candidate " + NAMES[index] + " at " + address);
            }
        } finally { decompiler.dispose(); }
    }
}
