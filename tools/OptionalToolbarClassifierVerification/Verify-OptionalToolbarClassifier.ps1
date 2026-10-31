param([string]$RoslynDirectory = 'C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore')

$ErrorActionPreference = 'Stop'
$qaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaOutputDirectory = Join-Path $qaRoot '.cache/optional-toolbar-classifier-verification'
New-Item -ItemType Directory -Path $qaOutputDirectory -Force | Out-Null
$qaRoslyn = [IO.Path]::GetFullPath($RoslynDirectory)
if ('SC4Evolution.Mod.Diagnostics.AutonomousTestDriver' -as [type]) {
    throw 'Use a fresh PowerShell process so previously compiled harness types cannot provide stale evidence.'
}
Add-Type -Path (Join-Path $qaRoslyn 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $qaRoslyn 'Microsoft.CodeAnalysis.CSharp.dll')
function Get-QaMethod([string]$qaFile, [string]$qaName) {
    $qaTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $qaRoot $qaFile)))
    $qaNodes = @($qaTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.Text -eq $qaName })
    if ($qaNodes.Count -ne 1) { throw "Expected one method: $qaName" }
    $qaNodes[0].ToFullString()
}
$qaOrganic = 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicResidential.cs'
$qaDriver = 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.cs'
$qaNativeSource = [IO.File]::ReadAllText((Join-Path $qaRoot 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.NativeDiagnostics.cs'))
$qaMethods = (Get-QaMethod $qaOrganic 'OnLog') + (Get-QaMethod $qaOrganic 'OptionalToolbarDiagnosticsJson') + (Get-QaMethod $qaOrganic 'BoundedModalText')
$qaJson = (Get-QaMethod $qaDriver 'Json') + (Get-QaMethod $qaDriver 'Bool')
$qaStub = @'
public class LoadingManager { }
public static class BuildConfig { public static string applicationVersion = "stub-not-runtime"; }
public static class SteamHelper {
 public enum DLC { PlazasAndPromenadesDLC, HotelDLC, RacesAndParadesDLC }
 public static bool IsDLCOwned(DLC unused) { throw new System.Exception("No Steam calls allowed in pure tests"); }
}
public class PopsManager { public bool telemetryStarted; }
public enum LogType { Error, Exception, Assert, Warning, Log }
namespace ColossalFramework { public class Singleton<T> { public static bool exists; public static T instance; } }
namespace ColossalFramework.UI { public class UIView { } }
namespace SC4Evolution.Mod.Diagnostics {
 public sealed partial class AutonomousTestDriver {
  private string _session = "owned";
  private bool OwnsCurrentCity() { throw new System.Exception("No runtime reads allowed in pure tests"); }
  private sealed class NativeHarness {
   private readonly System.Collections.Generic.List<string> _errors = new System.Collections.Generic.List<string>();
   private volatile NativeOrganicDiagnosticContext _optionalToolbarContext;
   private readonly System.Collections.Generic.List<string> _baselineOptionalToolbarDiagnostics = new System.Collections.Generic.List<string>();
   private int _optionalToolbarDiagnosticCount, _optionalServicePointCount, _optionalHotelCount, _optionalRaceCount;
   public NativeHarness(NativeOrganicDiagnosticContext context) { _optionalToolbarContext = context; }
   public void Log(string message, string stack, LogType type) { OnLog(message, stack, type); }
   public void Invalidate() { lock (_errors) _optionalToolbarContext = null; }
   public int Errors { get { return _errors.Count; } }
   public int Count { get { return _optionalToolbarDiagnosticCount; } }
   public string Report() { return OptionalToolbarDiagnosticsJson(); }
   __QA_METHODS__
  }
  __QA_JSON__
  private const string GamePin = "DF557994A03C93D8BB4A14F80B1FBCC0FB29D547658442445D078D069BCDD9FE";
  private const string ColossalPin = "34701F61DEF31F5F9DF79D573CE19E0BC52575879BCC395D51B39F6D746A58B9";
  private static NativeDiagnosticSnapshot Snapshot(string version = "1.21.1-f9", string game = GamePin, string colossal = ColossalPin,
    string owned = "owned", bool serviceAbsent = true, bool hotelAbsent = true, bool raceAbsent = true,
    bool plazasUnowned = true, bool hotelsUnowned = true, bool racesUnowned = true, bool telemetry = false, string error = null) {
   return (NativeDiagnosticSnapshot)System.Activator.CreateInstance(typeof(NativeDiagnosticSnapshot),
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
    new object[] { version, game, colossal, owned, serviceAbsent, hotelAbsent, raceAbsent, plazasUnowned, hotelsUnowned, racesUnowned, telemetry, error },
    System.Globalization.CultureInfo.InvariantCulture);
  }
  public static string VerifyOptionalToolbar() {
   var cases = new System.Collections.Generic.List<string>();
   System.Action<string, bool> check = delegate(string name, bool result) { if (!result) throw new System.Exception("Failed: " + name); cases.Add(name); };
   string[] messages = new string[] {
    "Assembly resolution failure. No assembly named 'ServicePointGroupPanel' was found.  [Serialization]",
    "Assembly resolution failure. No assembly named 'HotelGroupPanel' was found.  [Serialization]",
    "Assembly resolution failure. No assembly named 'RaceGroupPanel' was found.  [Serialization]" };
   var snapshot = Snapshot();
   var context = new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", "owned", snapshot);
   foreach (string message in messages) {
    var harness = new NativeHarness(context); harness.Log(message, "", LogType.Error);
    check("exact:" + message, harness.Count == 1 && harness.Errors == 0);
    check("callsite-unconfirmed:" + message, harness.Report().Contains("exactCallsiteConfirmed\":false"));
    harness = new NativeHarness(context); harness.Log(message + "\r\n", "", LogType.Error);
    check("trailing-crlf:" + message, harness.Count == 1 && harness.Errors == 0);
    check("raw-crlf-preserved:" + message, harness.Report().Contains("\\u000d\\u000a"));
   }
   var populated = new NativeOrganicDiagnosticContext("OrganicResidentialSaveReloadTestCity", "OrganicResidentialActive", "owned", snapshot);
   foreach (string message in messages) { var h = new NativeHarness(populated); h.Log(message, "", LogType.Error); check("explicit-populated-organic:" + message, h.Count == 1 && h.Errors == 0); }
   foreach (var invalid in new[] {
    new NativeOrganicDiagnosticContext("OrganicResidentialSaveReloadTestCity", "OrganicResidentialActive", "foreign", snapshot),
    new NativeOrganicDiagnosticContext("OrganicResidentialSaveReloadTestCity", "WaitingForNativeOwnedReload", "owned", snapshot) }) {
    var h = new NativeHarness(invalid); h.Log(messages[0], "", LogType.Error); check("populated-mode-invalid-context:" + invalid.Phase + invalid.OwnedSession, h.Count == 0 && h.Errors == 1);
   }
   { var h = new NativeHarness(populated); h.Log("Pushing before telemetry started.", "", LogType.Error); check("populated-organic-does-not-admit-telemetry", h.Count == 0 && h.Errors == 1); }
   string[] unexpected = { null, " " + messages[0], messages[0] + " ", messages[0].ToLowerInvariant(), messages[0].Replace("  [", " ["),
    messages[0] + "extra", messages[0].Replace("Group", "Group\n"), messages[0].Replace("ServicePointGroupPanel", "CHSharedState"),
    "Pushing before telemetry started.", "Error with RACE_TEAM_DRIVER:0: The id was not found in the localization files." };
   foreach (string message in unexpected) { var h = new NativeHarness(context); h.Log(message, "", LogType.Error); check("unexpected:" + message, h.Count == 0 && h.Errors == 1); }
   foreach (LogType type in new[] { LogType.Exception, LogType.Assert }) { var h = new NativeHarness(context); h.Log(messages[0], "", type); check("wrong-log-type:" + type, h.Count == 0 && h.Errors == 1); }
   NativeOrganicDiagnosticContext[] invalidContexts = { null,
    new NativeOrganicDiagnosticContext("SaveReloadTestCity", "OrganicResidentialActive", "owned", snapshot),
    new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "WaitingForNativeOwnedReload", "owned", snapshot),
    new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", "foreign", snapshot),
    new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", null, snapshot),
    new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", "owned", null) };
   foreach (var ctx in invalidContexts) { var h = new NativeHarness(ctx); h.Log(messages[0], "", LogType.Error); check("invalid-context:" + cases.Count, h.Count == 0 && h.Errors == 1); }
   NativeDiagnosticSnapshot[] invalidPins = { Snapshot(version:"other"), Snapshot(game:"bad"), Snapshot(colossal:"bad"), Snapshot(owned:null), Snapshot(owned:"foreign"), Snapshot(error:"unavailable") };
   foreach (var pin in invalidPins) { var h = new NativeHarness(new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", "owned", pin)); h.Log(messages[0], "", LogType.Error); check("invalid-pin:" + cases.Count, h.Count == 0 && h.Errors == 1); }
   NativeDiagnosticSnapshot[] invalidTypeOrDlc = { Snapshot(serviceAbsent:false), Snapshot(hotelAbsent:false), Snapshot(raceAbsent:false), Snapshot(plazasUnowned:false), Snapshot(hotelsUnowned:false), Snapshot(racesUnowned:false) };
   for (int i = 0; i < invalidTypeOrDlc.Length; ++i) { var h = new NativeHarness(new NativeOrganicDiagnosticContext("OrganicResidentialTestCity", "OrganicResidentialActive", "owned", invalidTypeOrDlc[i])); h.Log(messages[i % 3], "", LogType.Error); check("type-present-or-dlc-owned:" + i, h.Count == 0 && h.Errors == 1); }
   var stopped = new NativeHarness(context); stopped.Invalidate(); stopped.Log(messages[0], "", LogType.Error); check("invalidated-context", stopped.Count == 0 && stopped.Errors == 1);
   var bounded = new NativeHarness(context);
   for (int i = 0; i < 30; ++i) bounded.Log(messages[i % 3], new string('a',4095) + "\ud83d\ude00", LogType.Error);
   check("bounded-counts", bounded.Count == 30 && bounded.Errors == 0 && bounded.Report().Contains("\"recordsCapped\":true"));
   check("bounded-stack", bounded.Report().Contains("\"stackTruncated\":true") && !bounded.Report().Contains("\ud83d"));
   check("save-reload-optional-unchanged", snapshot.Classify(messages[0],new NativeDiagnosticContext("WaitingForNativeOwnedReload","owned",true)) != null);
   check("save-reload-requires-accepted", snapshot.Classify(messages[0],new NativeDiagnosticContext("WaitingForNativeOwnedReload","owned",false)) == null);
   check("save-reload-rejects-organic-phase", snapshot.Classify(messages[0],new NativeDiagnosticContext("OrganicResidentialActive","owned",true)) == null);
   check("organic-rejects-telemetry-even-started-pin", Snapshot(telemetry:true).ClassifyOrganicOptionalToolbar("Pushing before telemetry started.", context) == null);
   return "{\"checks\":" + cases.Count + ",\"passed\":true,\"boundedDiagnosticReport\":" + bounded.Report() + "}";
  }
 }
}
'@
$qaStub = $qaStub.Replace('__QA_METHODS__', $qaMethods).Replace('__QA_JSON__', $qaJson)
$qaCompilationSource = "using System.Collections.Generic;`nusing System.Globalization;`nusing System.Text;`n" + $qaNativeSource + "`n" + $qaStub
# Only the actual pure classifier/callback/serialization methods and inert API stubs
# are compiled in memory. No mod DLL, installed assembly or engine is loaded/invoked.
Add-Type -TypeDefinition $qaCompilationSource -WarningAction SilentlyContinue
$qaResult = [SC4Evolution.Mod.Diagnostics.AutonomousTestDriver]::VerifyOptionalToolbar()
$qaParsed = $qaResult | ConvertFrom-Json
if ($qaParsed.boundedDiagnosticReport.records.Count -ne 24 -or $qaParsed.boundedDiagnosticReport.servicePointCount -ne 10 -or $qaParsed.boundedDiagnosticReport.hotelCount -ne 10 -or $qaParsed.boundedDiagnosticReport.raceCount -ne 10) { throw 'Bounded diagnostic JSON/counters mismatch' }
$qaEvidence = [ordered]@{
    schema = 1
    scope = 'PureOptionalToolbarClassifierWithInertApiStubs'
    recordedUtc = [DateTime]::UtcNow.ToString('o')
    engineInvoked = $false
    installedGameAssembliesLoaded = $false
    modBuilt = $false
    checks = $qaParsed.checks
    passed = $qaParsed.passed
    sourceInputs = @(
        foreach ($qaRelativePath in @($qaOrganic, $qaDriver, 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.NativeDiagnostics.cs')) {
            [ordered]@{path=$qaRelativePath; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $qaRoot $qaRelativePath)).Hash}
        }
    )
    harnessSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash
    boundedDiagnosticReport = $qaParsed.boundedDiagnosticReport
}
$qaResultPath = Join-Path $qaOutputDirectory 'result.json'
$qaEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $qaResultPath -Encoding utf8
[pscustomobject]@{Checks=$qaParsed.checks;Passed=$qaParsed.passed;JsonRecords=$qaParsed.boundedDiagnosticReport.records.Count;EngineInvoked=$false;ModBuilt=$false;ResultPath=$qaResultPath}
