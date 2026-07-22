// Targeted single-function decompile (NO full auto-analysis).
// Resolve RVA via Inspector JSON / Cpp2IL first; change TARGET for other methods.
//@category StandChillow
import java.io.FileWriter;
import java.io.PrintWriter;
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.SourceType;

public class SmokeDecompileBqkg extends GhidraScript {
	// Il2CppInspectorRedux: Void gpe.bqkg() @ 0x0323BA54
	private static final long TARGET = 0x0323BA54L;
	private static final String OUT =
		"/home/vlal/standdrochillov/decompiled/ghidra_smoke_bqkg.c";

	@Override
	public void run() throws Exception {
		Address addr = toAddr(TARGET);
		FunctionManager fm = currentProgram.getFunctionManager();
		Function fn = fm.getFunctionAt(addr);
		if (fn == null) {
			fn = createFunction(addr, "gpe_bqkg");
		} else {
			fn.setName("gpe_bqkg", SourceType.USER_DEFINED);
		}
		if (fn == null) {
			printerr("Failed to create function at " + addr);
			return;
		}
		DecompInterface decomp = new DecompInterface();
		decomp.openProgram(currentProgram);
		DecompileResults res = decomp.decompileFunction(fn, 60, monitor);
		String code = res.getDecompiledFunction() != null
			? res.getDecompiledFunction().getC()
			: ("DECOMPILE_FAILED: " + res.getErrorMessage());
		try (PrintWriter pw = new PrintWriter(new FileWriter(OUT))) {
			pw.println("/* smoke target: gpe.bqkg @ 0x0323BA54 (LAN discovery host listen) */");
			pw.println(code);
		}
		println("Wrote " + OUT);
		println(code.substring(0, Math.min(code.length(), 1500)));
	}
}
