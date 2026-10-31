using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
public static class CivicPureChecks
{
    // METHODS
    static int count;
    static void Check(bool value,string reason) { ++count; if(!value)throw new Exception(reason); }
    static void Reject(Action action,string reason) { bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,reason); }
    public static int Run(string path)
    {
        int[] raws={0,1,100,101,300,1000}; int[] divs={1,100};
        foreach(int raw in raws)foreach(int div in divs)for(int p=0;p<=255;++p)
        {
            decimal f=p<=100?p/100m:(100m+(Math.Min(p,120)-100)/2m)/100m;
            int expected=(int)Decimal.Ceiling(raw*f/div);
            Check(EfficiencyCeiling(raw,p,div)==expected,"Rational ceil p="+p+" raw="+raw);
        }
        Check(EfficiencyCeiling(100,120,1)==110,"No binary-double phantom111");
        Check(EfficiencyCeiling(100,120,1)>EfficiencyCeiling(100,100,1),"110percent capacity expansion");
        Reject(delegate{EfficiencyCeiling(-1,100,1);},"negative raw");
        Reject(delegate{EfficiencyCeiling(1,256,1);},"invalid percent");
        Reject(delegate{EfficiencyCeiling(1,100,0);},"zero denominator");
        string common="schema=1\nassetName=NorthEurope1FINAL.Green Plains_Data\nassetChecksum=d09cc7008c7f95c0e55b1ebed0a80579\ntimeoutSeconds=300\ncontrolledQuit=true\ncaptureAllTabs=false\n";
        foreach(string mode in new[]{"CivicFundingTestCity","NewTestCity","SaveReloadTestCity","ResidentialClassTestCity","OrganicResidentialTestCity","OrganicResidentialSaveReloadTestCity"})
        {
            string text=common+"mode="+mode+"\n";if(mode.StartsWith("Organic"))text+="organicInventoryOnly=false\n";
            File.WriteAllText(path,text);Request r=ReadRequest(path);Check(r.Mode==mode&&!r.CaptureAllTabs,"mode parse "+mode);
        }
        File.WriteAllText(path,common.Replace("captureAllTabs=false","captureAllTabs=true")+"mode=CivicFundingTestCity\n"); Reject(delegate{ReadRequest(path);},"Civic capture prohibited");
        File.WriteAllText(path,common+"mode=CivicFundingTestCity\norganicInventoryOnly=false\n");Reject(delegate{ReadRequest(path);},"Organic field prohibited in Civic");
        File.WriteAllText(path,common+"mode=Unknown\n");Reject(delegate{ReadRequest(path);},"unknown mode prohibited");
        File.WriteAllText(path,common+"mode=CivicFundingTestCity\ntimeoutSeconds=600\n");Reject(delegate{ReadRequest(path);},"duplicate field");
        File.WriteAllText(path,common.Replace("timeoutSeconds=300","timeoutSeconds=601")+"mode=CivicFundingTestCity\n");Reject(delegate{ReadRequest(path);},"timeout bound");
        File.WriteAllText(path,common.Replace("controlledQuit=true","controlledQuit=True")+"mode=CivicFundingTestCity\n");Reject(delegate{ReadRequest(path);},"strict boolean");
        return count;
    }
}
