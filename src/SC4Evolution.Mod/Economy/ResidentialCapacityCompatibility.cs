using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SC4Evolution.Mod.Economy
{
    // Token-independent exact managed-body material. Runtime IL hashes remain
    // observations; only literal reviewed canonical hashes grant approval.
    internal static class ResidentialCapacityCanonicalIL
    {
        private static readonly Dictionary<short, OpCode> Codes = MakeCodes();
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static Dictionary<short, OpCode> MakeCodes()
        {
            Dictionary<short, OpCode> result = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode)) { OpCode code = (OpCode)field.GetValue(null); result[code.Value] = code; }
            return result;
        }
        internal static string TypeId(Type type)
        {
            if (type == null) return "<null>";
            if (type.IsByRef) return TypeId(type.GetElementType()) + "&";
            if (type.IsPointer) return TypeId(type.GetElementType()) + "*";
            if (type.IsArray) return TypeId(type.GetElementType()) + (type.GetArrayRank() == 1 ? (type.Name.EndsWith("[]", StringComparison.Ordinal) ? "[]" : "[*]") : "[" + new string(',', type.GetArrayRank() - 1) + "]");
            if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
            string identity = type.Assembly.FullName + "|" + (type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName);
            if (type.IsGenericType) { List<string> args = new List<string>(); foreach (Type arg in type.GetGenericArguments()) args.Add(TypeId(arg)); identity += "<" + string.Join(",", args.ToArray()) + ">"; }
            return identity;
        }
        internal static string MethodId(MethodBase method)
        {
            if (method == null || method.DeclaringType == null || method is DynamicMethod || method.Module is ModuleBuilder || method.Module.Assembly is AssemblyBuilder)
                throw new InvalidOperationException("Dynamic/unowned methods cannot be approved.");
            List<string> parameters = new List<string>(); foreach (ParameterInfo p in method.GetParameters()) parameters.Add(TypeId(p.ParameterType));
            MethodInfo info = method as MethodInfo; List<string> generic = new List<string>();
            if (method.IsGenericMethod) foreach (Type arg in method.GetGenericArguments()) generic.Add(TypeId(arg));
            return TypeId(method.DeclaringType) + "::" + method.Name + "<" + string.Join(",", generic.ToArray()) + ">(" + string.Join(",", parameters.ToArray()) + ")->" + (info == null ? "void" : TypeId(info.ReturnType)) + "|attrs=" + ((int)method.Attributes).ToString(CultureInfo.InvariantCulture) + "|impl=" + ((int)method.GetMethodImplementationFlags()).ToString(CultureInfo.InvariantCulture) + "|call=" + ((int)method.CallingConvention).ToString(CultureInfo.InvariantCulture);
        }
        private static string MemberId(MemberInfo member)
        {
            MethodBase method = member as MethodBase;
            if (method != null)
            {
                // An operand identifies a callee, not its implementation flags
                // (framework reference vs Mono implementation may differ).
                string identity = MethodId(method); return identity.Substring(0, identity.IndexOf("|attrs=", StringComparison.Ordinal)) + "|static=" + (method.IsStatic ? "1" : "0") + "|call=" + ((int)method.CallingConvention).ToString(CultureInfo.InvariantCulture);
            }
            Type type = member as Type; if (type != null) return TypeId(type);
            FieldInfo field = member as FieldInfo;
            if (field != null) return TypeId(field.DeclaringType) + "::" + field.Name + ":" + TypeId(field.FieldType) + "|attrs=" + ((int)field.Attributes).ToString(CultureInfo.InvariantCulture) + (field.IsLiteral ? "|literal=" + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : "");
            throw new InvalidOperationException("Unsupported metadata operand.");
        }
        internal static string Material(MethodBase method) { List<MemberInfo> ignored; return Material(method, out ignored); }
        internal static string Material(MethodBase method, out List<MemberInfo> references)
        { return Material(method, out references, delegate(Module module, int token, Type[] types, Type[] methods) { return module.ResolveMember(token, types, methods); }, delegate(Module module, int token) { return module.ResolveString(token); }); }
        internal static string RawMaterial(MethodBase method)
        { List<MemberInfo> references; return RawMaterial(method, out references, delegate(Module module, int token, Type[] types, Type[] methods) { return module.ResolveMember(token, types, methods); }, delegate(Module module, int token) { return module.ResolveString(token); }); }
        internal static string RawMaterial(MethodBase method, out List<MemberInfo> references, Func<Module, int, Type[], Type[], MemberInfo> resolveMember, Func<Module, int, string> resolveString)
        {
            string original = Material(method, out references, resolveMember, resolveString);
            StringBuilder state = new StringBuilder("RawHookILV2|"); state.Append(original);
            // Harmony binds special/native arguments by parameter NAME and
            // distinguishes out/ref attributes. Those are part of approval.
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                state.Append("|parameter=").Append(parameter.Position.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Convert.ToBase64String(StrictUtf8.GetBytes(parameter.Name ?? string.Empty))).Append(':').Append(((int)parameter.Attributes).ToString(CultureInfo.InvariantCulture));
                if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0) state.Append(':').Append(ParameterDefault(parameter));
            }
            foreach (MemberInfo member in references)
            {
                FieldInfo field = member as FieldInfo; if (field == null) continue;
                state.Append("|thread-field=").Append(TypeId(field.DeclaringType)).Append("::").Append(field.Name).Append(':');
                int count = 0;
                foreach (CustomAttributeData attribute in CustomAttributeData.GetCustomAttributes(field))
                    if (attribute.Constructor.DeclaringType.FullName == "System.ThreadStaticAttribute") { count++; state.Append(TypeId(attribute.Constructor.DeclaringType)).Append(';'); }
                state.Append(count.ToString(CultureInfo.InvariantCulture));
            }
            return state.ToString();
        }
        private static string ParameterDefault(ParameterInfo parameter)
        {
            object value=parameter.RawDefaultValue;
            if(value==null)return "<null>";
            // Mono returns a boxed enum for an enum default; metadata-only
            // reflection returns its underlying integer. Encode that same integer.
            if(parameter.ParameterType.IsEnum)
                return Enum.GetUnderlyingType(parameter.ParameterType).FullName=="System.UInt64" ? Convert.ToUInt64(value,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : Convert.ToInt64(value,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value,CultureInfo.InvariantCulture);
        }
        internal static string Material(MethodBase method, out List<MemberInfo> references, Func<Module, int, Type[], Type[], MemberInfo> resolveMember, Func<Module, int, string> resolveString)
        {
            string methodId = MethodId(method); MethodBody body = method.GetMethodBody();
            if (body == null) throw new InvalidOperationException("Approved methods require an ordinary managed body.");
            byte[] il = body.GetILAsByteArray(); int cursor = 0; references = new List<MemberInfo>();
            StringBuilder s = new StringBuilder("OwnHookILV1|"); s.Append(methodId).Append("|declattrs=").Append(((int)method.DeclaringType.Attributes).ToString(CultureInfo.InvariantCulture)).Append("|base=").Append(TypeId(method.DeclaringType.BaseType)).Append("|stack=").Append(body.MaxStackSize.ToString(CultureInfo.InvariantCulture)).Append("|init=").Append(body.InitLocals ? '1' : '0');
            foreach (LocalVariableInfo local in body.LocalVariables) s.Append("|local=").Append(local.LocalIndex.ToString(CultureInfo.InvariantCulture)).Append(':').Append(TypeId(local.LocalType)).Append(':').Append(local.IsPinned ? '1' : '0');
            Type[] typeArgs = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            while (cursor < il.Length)
            {
                int offset = cursor; short value = il[cursor++]; if (value == 0xfe) value = unchecked((short)(0xfe00 | il[cursor++]));
                OpCode code; if (!Codes.TryGetValue(value, out code)) throw new InvalidOperationException("Unknown IL opcode.");
                s.Append("|op=").Append(offset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(code.Name).Append(':');
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineI: case OperandType.ShortInlineVar: case OperandType.ShortInlineBrTarget:
                        s.Append(il[cursor++].ToString("x2", CultureInfo.InvariantCulture)); break;
                    case OperandType.InlineVar:
                        s.Append(BitConverter.ToUInt16(il, cursor).ToString(CultureInfo.InvariantCulture)); cursor += 2; break;
                    case OperandType.InlineI: case OperandType.InlineBrTarget:
                        s.Append(BitConverter.ToInt32(il, cursor).ToString(CultureInfo.InvariantCulture)); cursor += 4; break;
                    case OperandType.InlineI8:
                        s.Append(BitConverter.ToInt64(il, cursor).ToString(CultureInfo.InvariantCulture)); cursor += 8; break;
                    case OperandType.ShortInlineR:
                        s.Append(BitConverter.ToUInt32(il, cursor).ToString("x8", CultureInfo.InvariantCulture)); cursor += 4; break;
                    case OperandType.InlineR:
                        s.Append(BitConverter.ToUInt64(il, cursor).ToString("x16", CultureInfo.InvariantCulture)); cursor += 8; break;
                    case OperandType.InlineString:
                        s.Append(Convert.ToBase64String(StrictUtf8.GetBytes(resolveString(method.Module, BitConverter.ToInt32(il, cursor))))); cursor += 4; break;
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineType: case OperandType.InlineTok:
                        MemberInfo member = resolveMember(method.Module, BitConverter.ToInt32(il, cursor), typeArgs, methodArgs); cursor += 4;
                        references.Add(member); s.Append(Convert.ToBase64String(StrictUtf8.GetBytes(MemberId(member)))); break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, cursor); cursor += 4;
                        if (count < 0 || count > (il.Length - cursor) / 4) throw new InvalidOperationException("Invalid IL switch.");
                        s.Append(count.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < count; i++) { s.Append(',').Append(BitConverter.ToInt32(il, cursor).ToString(CultureInfo.InvariantCulture)); cursor += 4; } break;
                    default: throw new InvalidOperationException("Unsupported IL operand type: " + code.OperandType);
                }
            }
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                s.Append("|eh=").Append(((int)clause.Flags).ToString(CultureInfo.InvariantCulture)).Append(':').Append(clause.TryOffset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(clause.TryLength.ToString(CultureInfo.InvariantCulture)).Append(':').Append(clause.HandlerOffset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(clause.HandlerLength.ToString(CultureInfo.InvariantCulture));
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) s.Append(':').Append(TypeId(clause.CatchType));
                else if (clause.Flags == ExceptionHandlingClauseOptions.Filter) s.Append(':').Append(clause.FilterOffset.ToString(CultureInfo.InvariantCulture));
            }
            return s.ToString();
        }
    }
    internal static class ResidentialCapacityOwnHooks
    {
        internal const string OwnAssemblyIdentity = "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null";
        internal const string SimulationAssemblyIdentity = "SC4Evolution.Simulation, Version=0.2.0.0, Culture=neutral, PublicKeyToken=null";
        private const string HookType = "SC4Evolution.Mod.Economy.ResidentialClassPatches";
        private static readonly string[,] MethodPins = new string[,] {
            { "SC4Evolution.Simulation|other instance System.Boolean SC4Evolution.Simulation.ResidentialLotIdentity::Matches(SC4Evolution.Simulation.ResidentialLotIdentity)", "b6deeae51e479cec8f4f6972196aec71a1ee839a1e7a637572b46d95a78f1c34" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::.ctor(System.Guid,System.UInt32,System.Byte[],System.UInt64,SC4Evolution.Simulation.ResidentialLotRecord[])", "042017580172af5d4af0c4393fc7b390685d95bc68cb500383b653b7fb247ffe" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::.ctor(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ClassOrigin)", "66e2073ffef44a26d5dedd53e2ae81c7f96364e7a773a9f7ba3f16b2480ba640" },
            { "SC4Evolution.Simulation|other static System.Boolean SC4Evolution.Simulation.ResidentialLotIdentity::IsFiniteBits(System.Int32)", "aea863eabbb97e1d4c615e11501953b66a8a62a94309008a8dce934b91a9d258" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialClassLedger::ValidateClass(SC4Evolution.Simulation.ResidentialClass,System.String)", "612336bb83719eef034de1d4df9e6b6e3f3d5db292769ffa387d56f3f2b97fdb" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialClassLedger::ValidateOrigin(SC4Evolution.Simulation.ClassOrigin,System.String)", "efc872ed4bc1b0aa78d4f0011c564887b5adbf515d150a44eddde5fe8577490c" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialWaterProfile::ValidateCap(System.Int32,System.String)", "338a4beb711e6562b771b35a60a8f4e98f390a7f3a925244668c595abda97535" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialWaterProfile::ValidateSourceHash(System.Byte[])", "d9ae801e61d2368a4b38a5125aa845ee5c9d06ed1668ed495c29a38ec92d3f4b" },
            { "SC4Evolution.Simulation|private instance SC4Evolution.Simulation.ResidentialLotRecord SC4Evolution.Simulation.ResidentialClassLedger::AddGeneration(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ClassOrigin)", "d891cc2cf6b17ca10c3c7db3fe1c21547e2650227f14e02e8c37b73cb557e93b" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::set_LedgerId(System.Guid)", "326f5e978c23522268fe8af92a9cdb10302de34dcdf1bb58aeee7facc2ae058c" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::set_PolicyVersion(System.UInt32)", "6d788b2911e3a21d9c936ce94ecca5f351c124d51ff52f797f9cb82557e7c25d" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_BuildingId(System.UInt16)", "7d97579d03ba8a08ccd3d7f38d22e5b6ab51c4e0bc592accd0306aca70cd6f50" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Length(System.Byte)", "befd49031c3587d2fe8a95bf2bbcb1793045415b2dd5b0cf540dfc07f4f9dbd1" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_NativeBuildIndex(System.UInt32)", "f518b6b14c4403db89c7fca66775954cf1366438a0b4cece364b1a397985087e" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_OriginXBits(System.Int32)", "85cadaad65e2eb33d35ecb4fcda503bd735615cb53f4d21bd13246d9a2b3fb02" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_OriginZBits(System.Int32)", "da06333401df48d378ffe6b4eb89cc9b0c6178c45896b6ba02f8e4b0a83a91d3" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_PrefabName(System.String)", "6957d1e47a19204d838e8f1dab1b4fbdcdcdac4e36101145cc15ec2339cba62c" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Service(System.Int32)", "9eb0e16462956d3119d437f59fdfa062d0a492e2b089fffc51dfb717acdc9908" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_SubService(System.Int32)", "913550690ca42399c8f6f84986495179b4a54fa86d6308d237e5b903bd208854" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Width(System.Byte)", "c32358989a34c63ee834f61dfafec52862018de8c8c1bd948c17e9b2c64c8575" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_DesignClass(SC4Evolution.Simulation.ResidentialClass)", "6f746c42a192012495632190de09c061701540643878bf8dcadf6f1348a0202e" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Generation(System.UInt64)", "35dbe8303646245859640ab3121c66ba435ca95e4950b580b0c8ebe055138daa" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Identity(SC4Evolution.Simulation.ResidentialLotIdentity)", "4a3d9c0fabfc7bff2c502449a13affcc586c9a510e9d11f6af333a4749355127" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_OccupantClass(SC4Evolution.Simulation.ResidentialClass)", "3ddb98c5a1ef4e20504968e6837e5c5ac07724e609a36c2b1f87925de7cc22f3" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Origin(SC4Evolution.Simulation.ClassOrigin)", "3f1214bdde649e6fffaead8378f9f5904694e9dd93a0ec7ea29bcd1c8543a20c" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::BytesEqual(System.Byte[],System.Byte[])", "5a5237947f89d8cf775b1b06a9b17f145f1409f0ba20737b4e958b6943570731" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::IsValidClass(System.Byte)", "a270b7e78c39e27327b2579f8f46f7ec7fc900cb9f76b357c5de1e7a7aa4a8e4" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::IsValidOrigin(System.Byte)", "e01202bfe3d3ed8dcbfb4b6a5be2efe807f200c94a8b89bd0dfe139286b68790" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialSelectionPolicy::Equal(System.Byte[],System.Byte[])", "e55f484ba1493ce4022c8a1a7800edad4e435a860d377dac5c8d3d8eb15eef60" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialClassLedgerCodec::ReadBoundedPayload(System.IO.Stream)", "8a1c6f7e33ec38efac1d41cbe19bff381f541fc9c2975558f474944ba247194f" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::FromHex(System.String)", "c927950f34f402b5d1cc228cf085c46dcfb3feecc7c90bdc0b76de926a2a5e1d" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::Hash(System.Byte[])", "fa14070148877bfb922e3dbfdb3c2f4f0c3b00d6586d53bd94213024a25bd423" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialClassLedgerCodec::Crc32(System.Byte[],System.Int32,System.Int32)", "e841623af394a568d6204421e432a09f24518c786db3e16a967af8266ac94be9" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialClassLedgerCodec::ReadUInt32LittleEndian(System.Byte[],System.Int32)", "6342da9144ace53baa38c5234ca22249c8a9463b8062066ceb38b3043a038033" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialWaterProfileCodec::Crc32(System.Byte[],System.Int32,System.Int32)", "99e6c79896adc6524665980275d8657bebc44349d2afbe650823e38a817a96b5" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialClassLedgerCodec::.cctor()", "88ca6fb41aac0f0ccad703e698e7a58b5099822164c52213f0871a49b48a7618" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialLotIdentity::.cctor()", "abb7ee44cb04609cd7d49a450042361a78ff7b5ea137c0ab15bd6129b935df71" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::.cctor()", "59344a6b1ab094283c04244cfc20e2220a3caf1038af7d27bb9d4fb1b166706b" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::RequireKnown(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "de6e6470862fd26c5525a71c07b684eaba7a077df545b1665c3d429dca456ee9" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::.cctor()", "922f11ac35338266249d1190e0ccbab08274e951c47f8aef58d7eb1adbb0bf9f" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::WriteEntry(System.Byte[],System.Int32,System.UInt32,System.Int32)", "96859b98ce163870796cd7b38c55e9836457b2c60fc14e277952b69f764d414a" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::WriteUInt32(System.Byte[],System.Int32,System.UInt32)", "584344a381b3d828684cc36e7535c9037f361cc50275bde1d76966ee95d1016f" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ClassOrigin SC4Evolution.Simulation.ResidentialLotRecord::get_Origin()", "af94bbe8388a5c30738730e3ff9d4a57c0640cc14f26e0f227c0d1f91bf9a71a" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialLotRecord::get_DesignClass()", "8dd5fc42c35a1982e510f6657b2a29cae589c29b0482ee3d1362281b9dfd6472" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialLotRecord::get_OccupantClass()", "136d402a3a1a219326d053fa00f3cdc7f66c8e04dc58b293cb58031a50030405" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Simulation.ResidentialLotRecord::get_Identity()", "6f54da1a82dc34b66f8f2805959223988af2a8d95a17a7a3b8bde8a4e15f3820" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotRecord SC4Evolution.Simulation.ResidentialClassLedger::ImportExisting(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialClass)", "0b8270d25769a3cf16affe8b1e12bb0ded1bb8940fdf585f9d092860b184d63d" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotRecord[] SC4Evolution.Simulation.ResidentialClassLedger::GetRecords()", "6a349d603a5af3b14cb9d99b9c767175eb9a00de5a21040056e091b2743da44e" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::RefreshUpgradeIdentity(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotIdentity)", "0cd74cc0b262b833d8f3b1cceddd41561dc3869f2475734d255f8d88e5dad3e4" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::Remove(System.UInt16)", "dac4f8071f0139b216c3adb7fc3016c7289df30e9d91f404c019824739ee5da2" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::TryGet(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotRecord\u0026)", "315a1899be5118b0db1d2961a090264d34a5339c361542537d17c57fcf3d126f" },
            { "SC4Evolution.Simulation|public instance System.Byte SC4Evolution.Simulation.ResidentialLotIdentity::get_Length()", "4d1bccecd593d16707ce00eee9945fb3c2f0aa265335958943428b705dda81e0" },
            { "SC4Evolution.Simulation|public instance System.Byte SC4Evolution.Simulation.ResidentialLotIdentity::get_Width()", "29d172269a00bdfd308c27ae5d8dda23e68527cb794a0313d9f4ff849e2c4c26" },
            { "SC4Evolution.Simulation|public instance System.Byte[] SC4Evolution.Simulation.ResidentialClassLedger::get_PolicyHash()", "4fc64739ceecebda151d0b6eae1ea166e5bdd755d69553873183f554476767b9" },
            { "SC4Evolution.Simulation|public instance System.Byte[] SC4Evolution.Simulation.ResidentialWaterProfile::get_SourceSha256()", "48c751fa2e387fd1dc33c595cca0844e899fe7db8bb8db991310f380b382b3e5" },
            { "SC4Evolution.Simulation|public instance System.Guid SC4Evolution.Simulation.ResidentialClassLedger::get_LedgerId()", "a538599c354edb454d778100115e6cae8a2b3266afaba187f1e2ff65e374d056" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialClassLedger::get_Count()", "92b542b4a6a0d075061821396cc7f081deae03b5c42e6196af221861ba27321b" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_OriginXBits()", "51397e182fa069716f6594ea9f4482885502c99a343fa22f10914724c5e0bb5b" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_OriginZBits()", "f2cd3b4d2dd9085b70463f05dbe8054a6094afa1a6be5854dcddef3f9b05a7c4" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_Service()", "d83a477ba526c13554eae5d87bebc2e353bbf9568ba2e20360013ae0eb6629dd" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_SubService()", "21e128a5f1d56455c293b19298f65e7365793ef88fc174c02d00ac636387b476" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_HighMaximumStage()", "512453781191941bbad94ba4de00c56893784668295b505d76b64a336dff809e" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_LowMaximumStage()", "7521c24bc421493d6ecd484d978645e5f2841bf416840ce93a47629571d9417b" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_MediumMaximumStage()", "891ef400043fec5f1505f69bf1e315e3033a8b50bb6ccb3096e8832320d78921" },
            { "SC4Evolution.Simulation|public instance System.String SC4Evolution.Simulation.ResidentialLotIdentity::get_PrefabName()", "9d71d258c4e80b4aea21cb44362bd6f8b5cc7a8cce493d5e99dd137eb3cd3b2c" },
            { "SC4Evolution.Simulation|public instance System.UInt16 SC4Evolution.Simulation.ResidentialLotIdentity::get_BuildingId()", "51347d60fc8016d53784785d1db868caaa983f7f29a99f04f1f7d9f0049f13a2" },
            { "SC4Evolution.Simulation|public instance System.UInt32 SC4Evolution.Simulation.ResidentialClassLedger::get_PolicyVersion()", "2e696ae6168894b694374d3aa43b29f4a0189eba5e74a749b2542eb1bbac7464" },
            { "SC4Evolution.Simulation|public instance System.UInt32 SC4Evolution.Simulation.ResidentialLotIdentity::get_NativeBuildIndex()", "1b91fc95116d2d17254cd1ed0589f9cf1bd869f24889ab4ec4f7541bcef3d136" },
            { "SC4Evolution.Simulation|public instance System.UInt64 SC4Evolution.Simulation.ResidentialClassLedger::get_NextGeneration()", "df527a9a29062870ecdaaf23ae3d1033d671ea7ec3704c2a6ba465fb1b5ccfa9" },
            { "SC4Evolution.Simulation|public instance System.UInt64 SC4Evolution.Simulation.ResidentialLotRecord::get_Generation()", "00dd61b43fdba82989e7713646b588c09cd3f0c3fdc4e12a367174d650b29d3f" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::.ctor(System.Guid,System.UInt32,System.Byte[])", "a12b3f102787bf6533fa5b45a6ce9c898dc144ca542937d0bc1809a6283203cc" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::.ctor(System.UInt16,System.UInt32,System.Int32,System.Int32,System.String,System.Int32,System.Int32,System.Byte,System.Byte)", "0293f8368ba98826c58f2f70870cbf4a0879ab8b1525e8d646e70e6d45fccc95" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Simulation.ResidentialClassLedgerCodec::Read(System.IO.Stream)", "34d779ce39d934f9793272858134990bfa089cdbcc3def5756504c71ea817dba" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Simulation.ResidentialSelectionPolicy::MigrateKnownNativeSafetyV1ToWaterV1(SC4Evolution.Simulation.ResidentialClassLedger,SC4Evolution.Simulation.ResidentialWaterProfile)", "e1037adf313ef32d7f09bd288c599e231de7c114e28abe4e22e0511f86d36810" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialSelectionPolicyId SC4Evolution.Simulation.ResidentialSelectionPolicy::Identify(SC4Evolution.Simulation.ResidentialClassLedger)", "dd7746808ac0ba558bdec6390cc7fd13dda7b266c4ba2778a08f2f133345a5ff" },
            { "SC4Evolution.Simulation|public static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::PolicyHash(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "80365a9153a4a68d43ea304657bff675970b1014ec141544070dcbdf6a15b04d" },
            { "SC4Evolution.Simulation|public static System.String SC4Evolution.Simulation.ResidentialSelectionPolicy::Name(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "ec3d88f429e25597772d10754ccad6cd31c4a8eceab7189255731fcecb10f988" },
            { "SC4Evolution.Simulation|public static System.UInt32 SC4Evolution.Simulation.ResidentialSelectionPolicy::Version(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "a170f738e831154c8d11aac5786b799d6f5591cbccd81dc7974ed736c33cfa04" },
            { "SC4Evolution.Simulation|public static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::ValidateWaterProfile(SC4Evolution.Simulation.ResidentialWaterProfile)", "e6a2e41301ac1b3adef81071c32432e953c4ac10d78d1189ca6706ccee5905ac" },
            { "SC4Evolution.Simulation|public static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::Write(System.IO.Stream,SC4Evolution.Simulation.ResidentialWaterProfile)", "775a1f9cb4019bda8252ce3dad2d34872abb0185b98980c20765f9efae78eb46" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialClassBridge SC4Evolution.Mod.Economy.EconomicBridge::get_Classes()", "6ba4197e94ae72efacbb952307e506f6164e564aeb3960b64291bb58689e2f47" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Mod.Economy.ResidentialClassBridge::BeforeUpgrade(System.UInt16)", "d8f71abcd39687f4e52ba8030dfa06d6682356e9f25e755fb291e72d298884ab" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialWaterProfile SC4Evolution.Mod.Economy.ResidentialWaterObserver::get_Profile()", "64e29664bfaf0f3652e1ea757bd9c3b4971e9a1b8b6747cb01fe01462bd76a34" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialWaterObserver::get_IsValid()", "69a3a33e69692ab0806c52002367a3c4a103a3a9506f5b8662029a6112ec42ae" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)", "2f13a36257561a8562f87aed02c6b5043c602e69c0bb926039bae78cd463c56d" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Disable(System.String)", "3f3f80f0468e019741a3ef2797560b374c3fb666150331d0aee953d205a79c7a" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Mirror(Citizen\u0026)", "0d7c357c83593ca328ee04c64e0a0a282b1d60c54f4e552fe508804eeb55c278" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::MirrorUnit(CitizenUnit\u0026)", "29cec096244d1240610ba64babc758c2991a3cf61ee68f1b9c33d649879c3c82" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RejectBinding(System.String)", "acebb2ec20cdadf9178cfa521602c53d1426862868ded54e744a1da95695a7c4" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Released(System.UInt16)", "94140e002ca1fa9b2aa074954148fdd14ae94f5c98265343a31e7ce49c75cd1a" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::.ctor(System.Boolean,System.Int32,System.Int32,System.Int32,System.String,System.String,System.String,System.String,SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus)", "89a42db415722747f29f15811565f3264c4ce6be270694adf57d815c06eb6051" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::.ctor(System.UInt64[],SC4Evolution.Mod.Economy.ResidentialLocalDecision)", "22fce4b51e9db2c344b62b4e9ad7f29b993a9f6a734a4b07274d8e4d59a5400c" },
            { "SC4Evolution|other static SC4Evolution.Mod.Economy.EconomicBridge SC4Evolution.Mod.Economy.EconomicDemandExtension::get_Active()", "b8e65e11c44c31d4ee9ea776906c52c14108e93551b29dfadc6802ad7366b174" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryIdentity(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity\u0026)", "c44d6c6977091ae60ebf7f6c16a25c9bd91eefdad08572177ed46205c5082ada" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryMirrorValue(System.Boolean,Citizen\u002BWealth,SC4Evolution.Simulation.ResidentialClass,ItemClass\u002BLevel,Citizen\u002BWealth\u0026)", "2e8f836f9c377aa6b313ae0abf980c0f4187ffd7888aacb76d8fbdadc7642d59" },
            { "SC4Evolution|other static System.Int32 SC4Evolution.Mod.Economy.ResidentialClassBridge::FloatBits(System.Single)", "2c04456747e80f5231aa610020a158339655cd7257a5d359413395a6dffa12eb" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryRecordLocked(System.UInt16,SC4Evolution.Simulation.ResidentialLotRecord\u0026)", "1f29e2b1529d9983378913212a128b6550bbff97c158c2a9cb37d33d067e46cd" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::BindLocked()", "49813c745611a4e14e7bf096983e2f26ab8a779bf0eb2de6d86d314bf2d4cff9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::MirrorCitizenLocked(Citizen\u0026,System.Boolean)", "9d2377a7003966df125337859edeb06018fa2a75f7c0728fb8c7ca97d1ef1ccd" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::PublishLocked()", "f3b1bc4e8f84dec6fa37bcd0c82ffe7aad6b6f0d7dc7555d4e0d6a15c20e1f6f" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::QueueHomeLocked(System.UInt16)", "1453de834fa3badb4e5f82cb4493a53666fa20fd66ae7516af1f68bd466ba12f" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RecountOriginsLocked()", "bf3dfe8632d60ecf22b2b3e466e08b3ba96d21b66aec1f4a91d2f2c3d6f9d324" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RemoveRecordLocked(System.UInt16)", "635c96697be2c433deff179431806016e816fd5b78c08d947606906e8d9b048a" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::ReplaceLedgerLocked(SC4Evolution.Simulation.ResidentialClassLedger)", "cf4a5b66bd14b9abd905d9d001f8ae7ad35f80fe504cdad4251acabbcdc339c7" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_ConfiguredPolicy(System.String)", "9c91a31fdbe34bbc458bf491f0dfe12841030a1d599731ed25b9940efe7986e2" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_EffectivePolicy(System.String)", "57e41c4b42a3228d020378debf919d560340bbba723d0c7638faff8cc5bfe71a" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Enabled(System.Boolean)", "4173c3f5419d0b3a6786900bf0289a76184d5cdd66efcd5f6feafbcad58ace1b" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_ImportedCount(System.Int32)", "01137ef84957c64a61483f166ca5455e072b5b79cecee97730dbbfb353a1b2a1" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_LocalDecisions(SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus)", "3b851cd64cac9004c6d04b560a37e0c68315b17c35ec23dc638bfe6752811d61" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Policy(System.String)", "59ba498b3fcd377561a3bed920ea6942b1c45a58a959a2ca3ca6134880e0a828" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_RetainedPolicy(System.String)", "05ab013d1fce28e70610ac7e802f263da7ee00c331d3c5658efd1c9c415f8901" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_SelectedCount(System.Int32)", "57aaee614b231e1cc9b496dc67b772458acfc67f2feb1e8a4d6febfe23821d53" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Status(System.String)", "bf312e5aa35a7967b1395c6354caaabb2bcc8e11c2f87ebed350fdb6155eae1a" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_UnboundResidents(System.Int32)", "57c93b6c8506e0de292fae1146cf8406e5f58e4d5d907fb246f0f243838bc655" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_ControlledRejections(System.UInt64)", "2bfdc8a93019fb32bc64147a93f5859f49764502d6c44d18c70947db5047e97b" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_HighChoices(System.UInt64)", "9c6b0280d0a890abb5db73fe6c6932c9f4c0546730a7c24d80117395c3b70be9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_HighVetoes(System.UInt64)", "1ef368d1c1a0f9ac6587c35fd3027f4a89c797a645a343c12251231d549006b3" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_LastDecision(SC4Evolution.Mod.Economy.ResidentialLocalDecision)", "800b78115e35738ccb72c22a631e72cab3281972965bd1eca1ec706cd19c51a2" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_LowChoices(System.UInt64)", "548c863e5721465fb68e29fe3efc684d06e2b17bffdddc3b46558a8b500b799e" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_MediumChoices(System.UInt64)", "e123828c9c5ca8bbf94304f0deb30121c4ec6f0f09376612c13bd431ed3354f3" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_NativeFallbacks(System.UInt64)", "ba2668f6e1a1ae1c585ea4b560ae3168f260c026bc881468500769324c05e6b4" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Served(System.UInt64)", "49130a4a0a446187a8921ab679544b5c262e9659ccfd478b58fda2a837f270f4" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Unknown(System.UInt64)", "2b74d6163bb5e15115b9ceedebd8d848f2883cdfe80a18b02b83add09154b979" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_UnknownDomain(System.UInt64)", "b1f99fdb253c1faee67770d148bdda39ca2aa4e55321e01ed525e282c0648cd5" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Unserved(System.UInt64)", "9bd06a6a404d1bb5df327289aa68834c42c9e7dc4c417499ac0b8ebfd8a009f1" },
            { "SC4Evolution|private static SC4Evolution.Mod.Economy.ResidentialClassBridge SC4Evolution.Mod.Economy.ResidentialClassPatches::Active()", "095d4758546092e2c4af6ba36deda09129ac413caf87c3902d1ba8fa1a426ad0" },
            { "SC4Evolution|private static SC4Evolution.Simulation.ResidentialClass SC4Evolution.Mod.Economy.ResidentialClassBridge::Bootstrap(ItemClass\u002BLevel)", "f346ab55ad35b7cbb0f991ee2f6a9a7246a6b36b909ccdd91920eb0020d5c1b8" },
            { "SC4Evolution|private static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Mod.Economy.ResidentialClassBridge::PreparePolicyLedger(SC4Evolution.Simulation.ResidentialClassLedger,System.Boolean,System.Boolean,SC4Evolution.Simulation.ResidentialSelectionPolicyId,SC4Evolution.Simulation.ResidentialWaterProfile)", "36658bceb3f1c67207aa973f81ecd5063694199cd70287bd940405bcb8684efa" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::SelectionPolicyMatches(SC4Evolution.Simulation.ResidentialClassLedger,System.Boolean,SC4Evolution.Simulation.ResidentialSelectionPolicyId,SC4Evolution.Simulation.ResidentialWaterProfile)", "0509bb8be75c5945138768f2b6f8a4f94b838720afb432d66255f3c30fa8898d" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.EconomicBridge::.cctor()", "e57057fb4cf228271fee91d78a6fd32e0c963d2748965610128b1cff5fee0db6" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.EconomicDemandExtension::.cctor()", "ba9876bee9a37341727e0841a6f99d38826ce4aea1b023186596808e3f85638b" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::.cctor()", "f285fd5a476c00e4376df8595ecee1fc8267b28dff499f40350c8145a3b56d93" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::.cctor()", "a4ae4e2677b7881439731fae1adee9a64c3ae60db1a0951f86f920d88e02321f" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)", "54658523a75c0550d8c375bdd4e3a6fcb682a8e45487e02526067299a761e6fa" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::BeforeUpgrade(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity\u0026)", "a667319f9afd5215509b4436854138d6fa042a61a8aeabcb40c8a8ba74a86e26" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::Created(System.Boolean,System.UInt16)", "07e4c01d79674f8339318d9b4f240de20146f761a9c96e241df6f29671ffebbb" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeChanged(Citizen\u0026)", "93f3531ec0313720cb7fea74c414884ef046305f3c7b916834376675d14fd5dd" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeUnitChanged(CitizenUnit\u0026)", "f2ca3aafc59307b12e886a63d13ab2bbfd6612aef9cbdbed5ce6933277d45e7f" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::Released(System.UInt16)", "bbbb44ee14b6864a97eaa3af0e8532b3944376d9446997c4db637ef0c971a3cc" },
        };
        // Target, kind, exact owner, exact hook signature. Canonical body approval
        // additionally requires the complete reviewed own dependency set below.
        private static readonly string[,] Approved = new string[,] {
            { "public instance System.Boolean BuildingManager::CreateBuilding(System.UInt16&,ColossalFramework.Math.Randomizer&,BuildingInfo,UnityEngine.Vector3,System.Single,System.Int32,System.UInt32)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "private static System.Void " + HookType + "::Created(System.Boolean,System.UInt16)" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Prefixes", "SC4Evolution.ResidentialLotIdentity.v1", "private static System.Void " + HookType + "::BeforeUpgrade(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity&)" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Postfixes", "SC4Evolution.ResidentialLotIdentity.v1", "private static System.Void " + HookType + "::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)" },
            { "public instance System.Void Citizen::SetHome(System.UInt32,System.UInt16,System.UInt32)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "private static System.Void " + HookType + "::HomeChanged(Citizen&)" },
            { "public instance System.Void CitizenUnit::SetWealthLevel(Citizen+Wealth)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "private static System.Void " + HookType + "::HomeUnitChanged(CitizenUnit&)" }
        };
        internal static string[,] ApprovalMetadata { get { return (string[,])Approved.Clone(); } }
        internal static int ExpectedMethodCount { get { return MethodPins.GetLength(0); } }
        internal static string Key(MethodBase method) { return method.Module.Assembly.GetName().Name + "|" + ResidentialCapacityCompatibility.Signature(method); }
        internal static string ExpectedHash(string key)
        { for (int i = 0; i < MethodPins.GetLength(0); i++) if (MethodPins[i, 0] == key) return MethodPins[i, 1]; return null; }
        internal static int Match(ResidentialCapacityPatchObservation patch)
        {
            if (patch == null) return -1;
            for (int i = 0; i < Approved.GetLength(0); i++)
                if (patch.Target == Approved[i, 0] && patch.Kind == Approved[i, 1] && patch.Owner == Approved[i, 2] && patch.Hook == OwnAssemblyIdentity + " | " + Approved[i, 3] &&
                    ExpectedHash("SC4Evolution|" + Approved[i, 3]) != null && patch.HookCanonicalSha256 == ExpectedHash("SC4Evolution|" + Approved[i, 3])) return i;
            return -1;
        }
        internal static MethodBase[] RequireMethods(Assembly[] assemblies)
        { return RequireMethods(assemblies, ResidentialCapacityCanonicalIL.Material); }
        internal static MethodBase[] RequireMethods(Assembly[] assemblies, Func<MethodBase, string> canonicalMaterial)
        { return RequirePinnedMethods(assemblies, canonicalMaterial, MethodPins, new string[,] { {"SC4Evolution", OwnAssemblyIdentity}, {"SC4Evolution.Simulation", SimulationAssemblyIdentity} }); }
        internal static MethodBase[] RequirePinnedMethods(Assembly[] assemblies, Func<MethodBase,string> canonicalMaterial, string[,] methodPins, string[,] sourceIdentities)
        {
            Dictionary<string,string> expected = new Dictionary<string,string>(StringComparer.Ordinal);
            for (int i=0;i<sourceIdentities.GetLength(0);i++) expected.Add(sourceIdentities[i,0],sourceIdentities[i,1]);
            Dictionary<string, Assembly> found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (Assembly assembly in assemblies)
            {
                string name = assembly.GetName().Name;
                if (!expected.ContainsKey(name)) continue;
                if (found.ContainsKey(name) && !ReferenceEquals(found[name], assembly)) throw new InvalidOperationException("Duplicate own hook source assembly: " + name);
                found[name] = assembly;
            }
            foreach (KeyValuePair<string,string> source in expected)
                if(!found.ContainsKey(source.Key)||found[source.Key].FullName!=source.Value) throw new InvalidOperationException("Own hook assembly identity differs or is absent: "+source.Key);
            List<MethodBase> methods = new List<MethodBase>();
            for (int i = 0; i < methodPins.GetLength(0); i++)
            {
                string key = methodPins[i, 0]; int separator = key.IndexOf('|'); string signature = key.Substring(separator + 1);
                int typeEnd = signature.IndexOf("::", StringComparison.Ordinal), typeStart = signature.LastIndexOf(' ', typeEnd);
                Type type = found[key.Substring(0, separator)].GetType(signature.Substring(typeStart + 1, typeEnd - typeStart - 1), true);
                List<MethodBase> candidates = new List<MethodBase>(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                candidates.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)); if (type.TypeInitializer != null) candidates.Add(type.TypeInitializer);
                MethodBase selected = null;
                foreach (MethodBase method in candidates) if (Key(method) == key) { if (selected != null) throw new AmbiguousMatchException(key); selected = method; }
                if (selected == null || selected is DynamicMethod || selected.DeclaringType == null || selected.Module is ModuleBuilder || selected.Module.Assembly is AssemblyBuilder || selected.ContainsGenericParameters || selected.IsGenericMethod)
                    throw new InvalidOperationException("Own ordinary nongeneric hook/helper is absent: " + key);
                string observed = ResidentialCapacityCompatibility.Hash(Encoding.UTF8.GetBytes(canonicalMaterial(selected)));
                if (observed != methodPins[i, 1]) throw new InvalidOperationException("Own hook/helper canonical body differs: " + key);
                methods.Add(selected);
            }
            return methods.ToArray();
        }
    }

    internal static class ResidentialCapacityRawHooks
    {
        internal const string Policy = "RawHookILV2";
        private static readonly string[,] Sources = new string[,] {
            {"SC4Evolution", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null"},
            {"SC4Evolution.Simulation", "SC4Evolution.Simulation, Version=0.2.0.0, Culture=neutral, PublicKeyToken=null"}
        };
        private static readonly string[,] MethodPins = new string[,] {
            { "SC4Evolution.Simulation|other instance SC4Evolution.Simulation.DemandNodeState SC4Evolution.Simulation.DemandGraph\u002BNode::ToState()", "5f2bdd1213789eb4d914e2a0639e279ed0b27953d9037ed879946ddcd81d0a20" },
            { "SC4Evolution.Simulation|other instance SC4Evolution.Simulation.ResidentialCapacityRecord SC4Evolution.Simulation.ResidentialCapacityRecord::WithPhase(SC4Evolution.Simulation.ResidentialCapacityPhase)", "6bcb2e699a0cdb282b9f2531529206c9490a6a57d95566c256d672bca66136bb" },
            { "SC4Evolution.Simulation|other instance SC4Evolution.Simulation.ResidentialCapacityWindow SC4Evolution.Simulation.ResidentialCapacityWindow::Change(SC4Evolution.Simulation.ResidentialCapacityWindowPhase,System.Int64)", "b1801bf5f0416a0d62484d494655568621a72a384a927d85d0fdaf72d8e452db" },
            { "SC4Evolution.Simulation|other instance System.Boolean SC4Evolution.Simulation.ResidentialLotIdentity::Matches(SC4Evolution.Simulation.ResidentialLotIdentity)", "9ecf9786eee5ea8ac590f693b30cc550d1d715fa75645cb9e0a14fcd90fe5c83" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.DemandGraph\u002BNodeBackup::.ctor(SC4Evolution.Simulation.DemandGraph\u002BNode)", "17f6664bdb5482b32f64e1b18aab077b2c160f136809a1eace3ea1596e0fa1df" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.DemandGraph\u002BNodeBackup::Restore(SC4Evolution.Simulation.DemandGraph\u002BNode)", "0b2d95b83d9e7d99b7824d2d0813c28f3555eacc01c90fe42005c22833c3610d" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::.ctor(System.Int64,System.Int64,System.Boolean,System.Boolean,System.Int32,System.Int32,System.Int32,System.Int64[],System.Int64[])", "ade32aa05fbe8b9b6ed8680266527308883b38a8f8592e921e047d441eb28ab4" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::.ctor(System.Guid,System.UInt32,System.Byte[],System.UInt64,SC4Evolution.Simulation.ResidentialLotRecord[])", "12599e044ad734bbbd54e62e0f8fa3dc57f02f1d5e571726a9b42c63ae4ad6e2" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialHomeCountBounds::.ctor(System.Int64,System.Int64,System.Int64)", "531dd8040f0160dbe166d2b3d999315f9675d05bc20962640d515c618ab653b7" },
            { "SC4Evolution.Simulation|other instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::.ctor(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ClassOrigin)", "f25063be360aac4d91eedd4eca6c09f5021fb3f91f8fc014ffed674574132bd5" },
            { "SC4Evolution.Simulation|other static System.Boolean SC4Evolution.Simulation.ResidentialLotIdentity::IsFiniteBits(System.Int32)", "15ee27eb4efa766a82ffd22c18f771cfa568689ccfad00f1f9a52f13e6e08ade" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.DemandNumbers::RequireFinite(System.Single,System.String)", "a2a89a050f4d14a4697c3df2776dab972ae96dead02c8ad77704cf9422606538" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::CalculateQuota(System.Single,System.Single,System.Int64\u0026,System.Int64\u0026)", "fd580828d998b5084ed35aee12824454aa282549a603bc21d2b5b99a451f0ad5" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::ValidatePhase(SC4Evolution.Simulation.ResidentialCapacityPhase)", "b7e5a74d84ad877d4ce1ea7cd7fcc6abad543ee30e01815577612264e11e68e4" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialClassLedger::ValidateClass(SC4Evolution.Simulation.ResidentialClass,System.String)", "689a79ad7b121700c03e89648b26721e3426cc3fe1de887317dda46d002b6e96" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialClassLedger::ValidateOrigin(SC4Evolution.Simulation.ClassOrigin,System.String)", "9050429dc53a0c4068c33db20cf53d33ed0f757b61b210f7aee14b518de87e3e" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialWaterProfile::ValidateCap(System.Int32,System.String)", "4dd9fd695b832d0231d2ad8068c1811d255e6fda3565d712f5592e07dab80ec2" },
            { "SC4Evolution.Simulation|other static System.Void SC4Evolution.Simulation.ResidentialWaterProfile::ValidateSourceHash(System.Byte[])", "7f7c2689618b36fc387c8cfd17944f41a054b45c629b56d86c2723dbdbb733c6" },
            { "SC4Evolution.Simulation|private instance SC4Evolution.Simulation.DemandGraph\u002BNode SC4Evolution.Simulation.DemandGraph::GetNode(System.UInt32)", "346d0c61088c5b7b424fcf5facdfff92dabd03a301687ec80c815c1d16a1c8da" },
            { "SC4Evolution.Simulation|private instance SC4Evolution.Simulation.DemandGraph\u002BNodeBackup[] SC4Evolution.Simulation.DemandGraph::CaptureState()", "44ce1d5d6104d4e9b3481af2615d0c356ad4209015dc913cafc0635264029ebc" },
            { "SC4Evolution.Simulation|private instance SC4Evolution.Simulation.ResidentialLotRecord SC4Evolution.Simulation.ResidentialClassLedger::AddGeneration(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ClassOrigin)", "02da2d659117109c29aee3f4dcd24f6ff9ca3f9457b91f741a5272559f82e8a4" },
            { "SC4Evolution.Simulation|private instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::ChangePhase(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialCapacityPhase,System.Boolean)", "e13985e9681ad3f3dbb6a2d8fc15944a85797890250e1e0e6098f603a4c8b561" },
            { "SC4Evolution.Simulation|private instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Find(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialCapacityRecord\u0026)", "78c893772abab71359c5bf537ce1e56772534d64c9962bc5838722413b4af85e" },
            { "SC4Evolution.Simulation|private instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::IsCurrent()", "13d81fba25c300e410777ee55fd3ae4b3b33174659fcd5a20e6613e8376bef18" },
            { "SC4Evolution.Simulation|private instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Owns(SC4Evolution.Simulation.ResidentialCapacityReservation,System.Object,System.Int64)", "3b163624e61590bd2befbdf7b396384e59663dab13f1e9ed24fe55af5f4ab343" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.DemandGraph::RestoreState(SC4Evolution.Simulation.DemandGraph\u002BNodeBackup[])", "58d73ce62ffd4b602e0d32bc3a4767e7fc6ce5d320d941c45f90a5775c73fa14" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::CheckAdd(SC4Evolution.Simulation.ResidentialCapacityRecord,SC4Evolution.Simulation.ResidentialCapacityRecord)", "d9298d56095f6b5e9ca48271e6aa189bfa6b16e25999ee79c271bf5dac4cff5a" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::CloseOpenWindows()", "952337e98a62aab30cc0fe637ff86f7e38c6d6c296f0dfdfca5fc02d0afc7cd0" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::FinishToken(SC4Evolution.Simulation.ResidentialCapacityReservation)", "083b50e5e9733b5bd5d2bbb935b963b1eca68564d4c6a27f7cc5ee2d18e362fe" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::Replace(SC4Evolution.Simulation.ResidentialCapacityRecord,SC4Evolution.Simulation.ResidentialCapacityRecord)", "898f820443e2f227f182b501369f3f84c28da098dacf5ae065385e89576b9d81" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::RequireValid()", "23743b7f432fa791350f726519d2c35d151c15c9287c4860cb150768cd9d1872" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::ValidateRecord(SC4Evolution.Simulation.ResidentialCapacityRecord)", "e3cc36901e0407ad349c2bda9080eb93ecada0a795f6b532059cb8e6f9ded95b" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_ExternalRevision(System.Int64)", "83a7f5b0edd1277adf232c51127b304199b3b64c3558fc241479d48a1aed1a84" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_PendingReservations(System.Int32)", "722d84252ead68481c4c6ddd2d544ec272c45978f245fdc0b64e90486a035f60" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_PublicationCurrent(System.Boolean)", "1c8f59e2338d5bdbb5d7c56c4598a6c016e98d147800e98c904fd34cf8add4c3" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_Records(System.Int32)", "7ad3b8ed0d266701299861692fdd6345cdd92f64af8dfa453ac44c8e922ce9de" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_SessionValid(System.Boolean)", "4db176e36c2001159d1e52267f80ed4c0717268cd3eb59e6871b6f80aefee0f2" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_StockRevision(System.Int64)", "ee6f7d4abee786dc21b78664206fb81aaa128309c433f5131e0d16623116c646" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::set_UnresolvedCreations(System.Int32)", "1cf573bb02ebd29fc65740ad125539e9990284f7d733ad2a9523669720029ffb" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_CapacityQ(System.Int64)", "f8c047042dff69ad0ec005451b3492f10b33cc7cf77024b1ce1d6ba1e4054323" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Category(SC4Evolution.Simulation.ResidentialNominalCategory)", "742530c8881d663b12c20fdd56a612a13d1f4fb8507cee684467385be14047d3" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Class(SC4Evolution.Simulation.ResidentialClass)", "d4fbc7def2c6ee23b4bcd847b17eb50dccbd12818dbe3e558f3258913867d21b" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_ClassLedgerId(System.Guid)", "b164e6f23cc1cf2b1ec1106f01f6e39c17cae9e7ed9a52a6db4cd058d16a82b1" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Generation(System.UInt64)", "adcf08f8a84c626e748407284a9a9fa28c3ea48590ee75415e029f75112e6ec6" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_HomeCount(System.Int64)", "fde35bd3b4f0f1f7a82eae6fd2a807a570c39af7dbb1f5f70d7bfea2d4d02b93" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Identity(SC4Evolution.Simulation.ResidentialLotIdentity)", "955f34a129839be9899f2e2a2228e6936ac95cb69894eb8863374f4e7d11ea08" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_NativeLevel(System.Int32)", "4c4f776e7802d95ace7967fe7847eed6a4fbab115685cc5e308275d65f69c7c2" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Origin(SC4Evolution.Simulation.ClassOrigin)", "65d482a1cf3ae71bba39ac2adb34a1935d931f9f485f4291dc1e9282a4527307" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::set_Phase(SC4Evolution.Simulation.ResidentialCapacityPhase)", "635118cd860eafb213cc0d29c185e9ea9c78acd484116472db9b4e475e931d77" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_Class(SC4Evolution.Simulation.ResidentialClass)", "41166d5ba3ccd5388e953d295899ff34939fcf42c0220d45713a4eaf8d1c31a1" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_CommittedQ(System.Int64)", "d0509642b65020db67671a41ffda98d05a1a5f53f50c3b2a570ba2458bd94b1f" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_Day(System.Int64)", "37f43f84ca85007eeda5cd47a6a149cea9c2d51b3836d5063798bc95ab963455" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_DesiredMaximumQ(System.Int64)", "b32565a70e701455be48f98fc7b3204b86ba4529ea10b12ec99c1c30ac436049" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_DesiredQ(System.Int64)", "e29d2ba1df8896aa82e3fa52dd6b1848bae4d2f1ee15c70bccf586f60cc218d9" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_OpeningExternalRevision(System.Int64)", "8b83afb1d9684946c15411948450c659fc2d0f0a99abb3c0a4d325d5b07bdd9f" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_OpeningRaw(System.Single)", "824ac937c829d2bd246ad131c42251278d5a8f0790d7a6fa0229197fd817b618" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_OpeningStockRevision(System.Int64)", "b8ef3047f361a54869bf7d80186bd4276dfec05b0287585b8fb49d3256498fa7" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_Overbuild(System.Single)", "56803861fa2db9275d0211f10675261d3dabd9cc3f062d914ca4cb6729b700fe" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::set_Phase(SC4Evolution.Simulation.ResidentialCapacityWindowPhase)", "d2f4478164e40fd6d908c28d92762a964285c753d5f821af2d9a8cefe0c3fd44" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::set_LedgerId(System.Guid)", "c0c477ab72c1629d4ce1da619ef54fa0a699f9bac8301fd1bef4360658f777bf" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::set_PolicyVersion(System.UInt32)", "64810e60986acc7863f061f937f79700639f7c174bf2adeaaa3b2c4c0f209ce8" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_BuildingId(System.UInt16)", "043191d13754fa576f5f10683b509fae5750a15f213abe11e8d4c6732b5a4112" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Length(System.Byte)", "502f0cdf1f04749106072b472eae529f3f701284d4d04c3821e97e046c3649d1" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_NativeBuildIndex(System.UInt32)", "2a2dd249bed0cb55a3319f06396bfc53fa6cd91bb9c2cfa78635999a48ab3dac" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_OriginXBits(System.Int32)", "b4a9f99b04ba699a1c346bfe6feb116b0670aa4dda3de4c5c8ca9d6b420166a7" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_OriginZBits(System.Int32)", "e3a3955ee941a74004a21dcc3bcc2df067636d138d64607162bb5d9e0ae8d651" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_PrefabName(System.String)", "c05a2e4c45cf85377e50af0a8e23b3a626d1a0618690e489916b94fcb3a46e83" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Service(System.Int32)", "a44e78f1ff37f8b01a00f269144204ddba4bda8943435365467432bc48d3c643" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_SubService(System.Int32)", "a920f72ca69662a0c070356f0c3b477fce657c1e91080f35faaa57996f7ae677" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::set_Width(System.Byte)", "5d571fe590138b4ef9e579c2cfe849936cda5e53ac8bb4a6a8c3184dc03c349e" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_DesignClass(SC4Evolution.Simulation.ResidentialClass)", "2d42ff2f6409fc09f1160e4820848fe43b287d2255ebf6a2fb0bd55707a8227e" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Generation(System.UInt64)", "4da12d143b7913a0bd631c5fd4f5675454f494c7bba8dc16b0a466e59c84f2e5" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Identity(SC4Evolution.Simulation.ResidentialLotIdentity)", "8c4d2eb33a8e8b1fad056ce1fe6afb60376375e021a3108e8268c54339d9a42d" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_OccupantClass(SC4Evolution.Simulation.ResidentialClass)", "baedaaa27f267272cf9ed6969cf20e2e1980f27361762932cc0ba32f1d4caa1b" },
            { "SC4Evolution.Simulation|private instance System.Void SC4Evolution.Simulation.ResidentialLotRecord::set_Origin(SC4Evolution.Simulation.ClassOrigin)", "e5b7a95a38223a7547f2916a35bc2b976e68f1aab917d6701545b0e44164f066" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.DemandGraph::Transfer(System.Single,System.Single,SC4Evolution.Simulation.DemandGraph\u002BNode,System.Boolean)", "2fd2f97a492d255207008e9ecb6cbbd07f7fa35d92336889efc5c530c25dd028" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::SameRecord(SC4Evolution.Simulation.ResidentialCapacityRecord,SC4Evolution.Simulation.ResidentialCapacityRecord)", "744fd5841c4f81d1c8e142d79f618772ddeaee4d17b58f4ec32696b9135437e3" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::BytesEqual(System.Byte[],System.Byte[])", "449f7ab1f26d7d018002b749a6bd43ecd36da971d3f5aa421749800dce008454" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::IsValidClass(System.Byte)", "169b026a722cab09a1fbf34537fd31c4f895d3e4764b2ef981188d7c04ceb717" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialClassLedgerCodec::IsValidOrigin(System.Byte)", "a7eb684f7e79570fa079fa222de343ade7bb10c2878d5d4e39c42234268d2313" },
            { "SC4Evolution.Simulation|private static System.Boolean SC4Evolution.Simulation.ResidentialSelectionPolicy::Equal(System.Byte[],System.Byte[])", "8a1d27e1ad3e95bdcf646246cddcad5853e37e2b84e3a40b36278d3990624846" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialCapacityLedger::HashPolicy()", "013a8729f9368c3b16746414da60420a51a7685825a679fd9c23028589947a85" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialClassLedgerCodec::ReadBoundedPayload(System.IO.Stream)", "3f533b81c67ee3ffd22dacff167fb6acdc669be81ab1369fab1ad7ea384ff4d9" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialNominalCapacity::ComputeHash()", "de2be31c3b8dc6d615e47eab5646f044cfac22b14775aacf0147ce7a8c3baf0b" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::FromHex(System.String)", "52242037d5ba292789d66b79b9711ef67509b149bb5e210db811ff47d4799aa3" },
            { "SC4Evolution.Simulation|private static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::Hash(System.Byte[])", "3d0cda3ea1d57653d11e401c588e1f940cfc687bb89e34c9688abc283b559553" },
            { "SC4Evolution.Simulation|private static System.Int32 SC4Evolution.Simulation.DemandGraph::NormalizeDemand(System.Single,System.Single,System.Single)", "268ce136a2923d48913035658e5bf4b61057964340db7f3f0db604445ce045bb" },
            { "SC4Evolution.Simulation|private static System.Int32 SC4Evolution.Simulation.ResidentialCapacityLedger::Index(SC4Evolution.Simulation.ResidentialClass)", "a963c26261cd98ddb183480f1401a07a3ce078d6906906adcff2579cdba837ed" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Abs32(System.Single)", "e96812948376921ce6446d873e96b1e1a2f27c83006034620ff171cf5c7d9573" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Add32(System.Single,System.Single,System.String)", "84c6a5ee269bea9d906d7b55d500d22998e2cf874080fdc93406460b21beac30" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Clamp(System.Single,System.Single,System.Single)", "6d41e6ba48923375345ad5e41df8b9c8dfb3dcdf816dd13ea73291773f0cf178" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Divide32(System.Single,System.Single,System.String)", "f7ad87c733ad2606f3c8bb827fbf29ce938789aff4da8a62a07cf20d06baebac" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Max32(System.Single,System.Single)", "7c54029d66dc4d3b613e0402cb109bb99068b64e1edac99c051f6378fffa263a" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Min32(System.Single,System.Single)", "fa2de5a4a00acd56789295d7c5ae39b5c51e7a91d49587a334942abd7ae69173" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Multiply32(System.Single,System.Single,System.String)", "b7818e87eaae87cafd76e6da85e3b01d6803ef45e9b46ce5c34750cdb9730182" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::RequireFinite(System.Single,System.String)", "6c14f8ac812619df59c0b5667b7324dd2bc0cee703a07927b28fa990adde3358" },
            { "SC4Evolution.Simulation|private static System.Single SC4Evolution.Simulation.DemandGraph::Subtract32(System.Single,System.Single,System.String)", "255e526611e6edca1d02f89a0dcf97d1b7faa6f7dc4bc6fec9c9feaf81481978" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialClassLedgerCodec::Crc32(System.Byte[],System.Int32,System.Int32)", "58da1414e85eb1676de61095c9d37ffdd1a5e75b13b1d81f5aef2662c392766a" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialClassLedgerCodec::ReadUInt32LittleEndian(System.Byte[],System.Int32)", "68ae29e55715977067668de5c69e518b13220de619e7c6cb9677be4ff82734a5" },
            { "SC4Evolution.Simulation|private static System.UInt32 SC4Evolution.Simulation.ResidentialWaterProfileCodec::Crc32(System.Byte[],System.Int32,System.Int32)", "6635e9882eedac48ef6c33cec51ec13631213a92f473068373ce148b1989560a" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.DemandGraph::ApplyCap(SC4Evolution.Simulation.DemandGraph\u002BNode,SC4Evolution.Simulation.DemandGraph\u002BNode)", "fbc6d7709b1ad1e27c5f0d31e739c322a45e8682fa0d830042d6a18eb5fc2ee0" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.DemandGraph::EndOfCycle(SC4Evolution.Simulation.DemandGraph\u002BNode)", "ace964e5e09462f834d2e4d039c0bc521f61e1d08872b745180a27a23f86850b" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.DemandGraph::RecalculateActiveDemand(SC4Evolution.Simulation.DemandGraph\u002BNode)", "d4aab78c62c861d5443a8111074132a7452c3d70d31b5275fe89bd8057dabdd4" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialCapacityLedger::.cctor()", "93404e440b8a3ac4bf71890b3a2cdd4e1c264fd171214199ccd400102f74bdab" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialClassLedgerCodec::.cctor()", "549e53cbe4bc3268b73ade4a7c73efe16500a76dcb9cdf565ce3b40a17bd4db8" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialLotIdentity::.cctor()", "878a25cbaccd2da8c7cd5d264041c4768eee1f78d94a448af4b25ddedb736291" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialNominalCapacity::.cctor()", "c7daeaebaed265599c82e3853b8d421a05afea1c43279dd85fb29d497bd3153b" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::.cctor()", "4b9bd26afe7eff126374fb93b5f1edccf2fbba61c1b9def30a63a9a3517ebb7c" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::RequireKnown(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "7adbfca9ec534958d4886aa80c8b8689d65dc921dd6d81bc6efa35cfc5ac60b7" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::.cctor()", "52cc23774b3733e980ac16163b0b6ead5dbb3380c5ad05310ac89fe06a97e6fb" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::WriteEntry(System.Byte[],System.Int32,System.UInt32,System.Int32)", "8d499b5e3325f655ffeb58a88ea81c75a00710ee5fca4497cacf65f8aeadc5af" },
            { "SC4Evolution.Simulation|private static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::WriteUInt32(System.Byte[],System.Int32,System.UInt32)", "5282aebdc8d2e0661456a142f81e2c4679f5e3110b54096609c5db02ac19ee25" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ClassOrigin SC4Evolution.Simulation.ResidentialCapacityRecord::get_Origin()", "4b3eda4e0bd84884f611c8ae4c784628ad1b28ec85475232ddeaa4ace7c2c538" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ClassOrigin SC4Evolution.Simulation.ResidentialLotRecord::get_Origin()", "2196961047501989a70295949cfbca2d0e0caf04f05cadaa71166a583f0dc508" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.DemandLinkKind SC4Evolution.Simulation.DemandLink::get_Kind()", "70ed56c32241cf79a9c9fbd740b196dfe1d535ec5f43ee68c300f5f9b2f05285" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.DemandNodeState[] SC4Evolution.Simulation.DemandGraph::GetAllStates()", "c06c822ec1e7bd08fd87e3ec118732212a9aa0041a6c9981f066e6ad5c1c5f47" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.DemandPropagationResult SC4Evolution.Simulation.DemandGraph::Propagate(System.Int32)", "af5b3c05749dbcdb8df2aa2aa8f8621636aed63ce6fcf1cefba20b9937006fbe" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot SC4Evolution.Simulation.ResidentialCapacityLedger::Snapshot()", "fd7d1b2d273e69fac30c9684ac1d2aeb2303ba086dd5941932767ffc4e162c96" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialCapacityPhase SC4Evolution.Simulation.ResidentialCapacityRecord::get_Phase()", "86296ad5f548cb7ac4ef9fd8ec78d8af04593096bf15674c8056ff7d8b36d693" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialCapacityWindowPhase SC4Evolution.Simulation.ResidentialCapacityWindow::get_Phase()", "688793a79d62c6767ffeeca7078a885ce46757c0883158660a4dc1420ee94a0d" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialCapacityLotSpec::get_Class()", "6f7252ddc5e5a0c178638853d2d392aab2b6c8fc2cb0b97b138645f00874b292" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialCapacityRecord::get_Class()", "d45eb34ed76603d19c379f3581d9cba2eaff1607d22612f6b9cc328cd73b901a" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialCapacityWindow::get_Class()", "b2a57c8e9ea12215322b84cb7b2699ec09b90958b4b45a447dda664b75e96f83" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialLotRecord::get_DesignClass()", "5ab1deb71997f1d603a50a87745cfe187df56d2fdfa89e691d7c6b5a7f2dce61" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialClass SC4Evolution.Simulation.ResidentialLotRecord::get_OccupantClass()", "a20ac834cd0c085d0fe79ba8bd3bea70813372d83b3765c78ba613cb0eddb52c" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Simulation.ResidentialCapacityRecord::get_Identity()", "623d2d053b12ad31c6200fe3c445cae99c8879a324e7b4406c9f332dfeb88f12" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Simulation.ResidentialLotRecord::get_Identity()", "d4a2c0de1fc7a450d1e78a76a260624d3824bf0ce0ac7a921c583c396b752eb2" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotRecord SC4Evolution.Simulation.ResidentialClassLedger::ImportExisting(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialClass)", "6c86c14e56419c3d172f88b08851e5d700e5b348ff7777501762cdef8ff64a4d" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialLotRecord[] SC4Evolution.Simulation.ResidentialClassLedger::GetRecords()", "312c870025d1ffe0be9a2e518ebd8c02a20e1e238142045f0cf2df8ca3dd1f63" },
            { "SC4Evolution.Simulation|public instance SC4Evolution.Simulation.ResidentialNominalCategory SC4Evolution.Simulation.ResidentialCapacityRecord::get_Category()", "2229fe0c96830432f1fd414170ba27f5de683f31341a6092860608a9e6fb781a" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Abandon(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64)", "34b3425e97da8364c15d7abea7100ff17d0e3fb31831d7fd50174e0e3271b680" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::AcknowledgePublication(System.Int64,System.Int64)", "3c8ce17acca41404a7f207fad537df1df93230115bd2f3a17f175122494b8ab5" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Cancel(SC4Evolution.Simulation.ResidentialCapacityReservation,System.Object,System.Int64)", "38138095cdcf2328fc394200cdefc032878560a1c788ef94f20b9c8b6407d37c" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Complete(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64)", "22ff801dea3cb05bfd4593337e812c02d8873bb46724c43c7b73f075e32749f8" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Import(SC4Evolution.Simulation.ResidentialCapacityRecord)", "e43c819298ec1cb17820878405757b89028b13bd097079c168bd95ce9ee9f0a7" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::RefreshUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialCapacityRecord)", "905064d587a75c7c9d01912afd76a2b2b592a79e0912cb80ed8a49b7644f6b6c" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Release(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64)", "c9c78f3acebf8ad1bcaa86b29ef17511368ec302b82e435488705fd67650c8bb" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedger::Reoccupy(SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialCapacityPhase)", "35241820d88330eedbbc8c3a3f91813651f59764fc8c0f37097dd08f2c7f489a" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_PublicationCurrent()", "22fc5930f7af9eae40860b4f23778ab05202e5d8f88087843903311da1c83f4f" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_SessionValid()", "36d93a6352c7f855d51fb40b3fde52232c34d9889b07d3bdfdc0921a6c9b9da9" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialCapacityRecord::get_IsActive()", "191996c38093afd9d0cd06877a57fdbe0e7cab656ebf1c8b451be40665cf18e4" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::RefreshUpgradeIdentity(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotIdentity)", "33375041b9c0d396daf394cdbbc80d6f363f9ddca3555f82dc1248f2e5e2ee1e" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::Remove(System.UInt16)", "a1c6fa833fcf377cd02b8c91faaad8393a5821519588cc5ac1427719019d290b" },
            { "SC4Evolution.Simulation|public instance System.Boolean SC4Evolution.Simulation.ResidentialClassLedger::TryGet(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotRecord\u0026)", "0a5f40fdb02cdf55546ff68a6a7ad1af89a99285e741d284d79b823644e93275" },
            { "SC4Evolution.Simulation|public instance System.Byte SC4Evolution.Simulation.ResidentialLotIdentity::get_Length()", "fe6913a63828add28affd0ad2ed45aeebe2c6d9a1d6260daa13e13be3eba4ef6" },
            { "SC4Evolution.Simulation|public instance System.Byte SC4Evolution.Simulation.ResidentialLotIdentity::get_Width()", "cc90699446a0beb8a02e3614c7f6f5d7082113aad2d63d4196d305f343cab907" },
            { "SC4Evolution.Simulation|public instance System.Byte[] SC4Evolution.Simulation.ResidentialClassLedger::get_PolicyHash()", "36f8438763beccdd8c2f9b65fb726a17ea549973ab7f9dddfd7c7ed1c08a785a" },
            { "SC4Evolution.Simulation|public instance System.Byte[] SC4Evolution.Simulation.ResidentialWaterProfile::get_SourceSha256()", "8c633a492612d08002cecca814d1071eac51f9ff08151655bf446209eae34aa8" },
            { "SC4Evolution.Simulation|public instance System.Guid SC4Evolution.Simulation.ResidentialCapacityLedger::get_ClassLedgerId()", "6bad41fd652cf4dafa05257d0d3a07aea735384ade0cc4b9368c79facac78a40" },
            { "SC4Evolution.Simulation|public instance System.Guid SC4Evolution.Simulation.ResidentialCapacityRecord::get_ClassLedgerId()", "f1851412ad7d2490098da6f5b28e05f02f98d8c4f6dc83287d9f66a33429a986" },
            { "SC4Evolution.Simulation|public instance System.Guid SC4Evolution.Simulation.ResidentialClassLedger::get_LedgerId()", "027cc748e2804acae668b2b3e646eccc457fc8b4403fc3de42645c67a6c64a88" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_PendingReservations()", "cbcf94d4cb9d3f1c1a84519af0d30802c5a9c09d88de141c587615222ce7ad22" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_UnresolvedCreations()", "3a10caedca63322922533aa27070d51e4569c207f898f53a9dc03b8ba08fcbbf" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialCapacityRecord::get_NativeLevel()", "99e3d1ddefdfc2449f0185da322f7c7b73100e8affa60a1f4725793d5a343749" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialClassLedger::get_Count()", "16c6ce7b937755cb441000f368a067ecaabdf3ce04acd85ea72380c70d8c4faf" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_OriginXBits()", "9a10ab9eee6893534f7f9e538986ec250869e5ceccf60759644a4fb0503fe51b" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_OriginZBits()", "939d980dc42003e0e57b7812ea241f73dde2222b7514260d9ec23d0d85c5703b" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_Service()", "3a47331f9fe03feee87d44c8a1e6abbbe761270f44a81d3833b2f6a37d4bcdec" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialLotIdentity::get_SubService()", "8f80396478ebeb01e600b61422529a41bac235ea3fb443473a8839f9e732659c" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_HighMaximumStage()", "e91875cb942fe12c8634089a61ab60f23b6b8485668225463370745a1279e434" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_LowMaximumStage()", "e7c600f67071d472121f192e819e2e1a4835cce12c65acb9289235649a666cfa" },
            { "SC4Evolution.Simulation|public instance System.Int32 SC4Evolution.Simulation.ResidentialWaterProfile::get_MediumMaximumStage()", "92f7487ae30b9398d12db4d7d6a0092d069ad1f0c67d735f35757e405fdd1635" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_ExternalRevision()", "9fc981f637686f518aa97250bc12b1e26dc9c4752f221746aa1fdd822560d906" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_StockRevision()", "f8d2d6fb4cca9005063a68c6d896a9a335422f4a52e119d4973dea2a0f6f7381" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityRecord::get_CapacityQ()", "c160c6f14c5afcb565ce8d8da9335fced6b3675ac0cd6c05eda0b86d57c089ff" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityRecord::get_HomeCount()", "ea47000bf41d89674d68b1f505ef61372abf937636a040e8127176d5a99253cd" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityReservation::get_ReservedQuotaQ()", "9ff803ae1a1e488b44ae72bc962f0d2904fe6a841c32efe01c30829429acedd7" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_CommittedQ()", "4c8d159b239500a537d65ad98cd354c275da204c8d592cdb0998692e3fc20172" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_Day()", "3263d3110d89f979e6632dc6095e3781df4f7693e618c47cd7155161c7989cde" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_DesiredMaximumQ()", "e7e3bcfd82a1bae0faf004432dbc32308ba7628b2cdc38f103c8acd1238d9c98" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_DesiredQ()", "5d4d3357dc4517495eca220d6ac2bab2f567a6e2debf0c926ac0f42b6e49a991" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_OpeningExternalRevision()", "14ebf2273de3ce208971ce227dc652095c9e199f1369e49795056f960ab0b2dd" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialCapacityWindow::get_OpeningStockRevision()", "5978861949c48f313574769b786f8ca229063f58df15acb790c58ba6116856b5" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialHomeCountBounds::get_MaximumHomes()", "4218b69fdbecb2b039db9dd458c61013b23a2912308c770bed5dc81e07a9a747" },
            { "SC4Evolution.Simulation|public instance System.Int64 SC4Evolution.Simulation.ResidentialHomeCountBounds::get_MinimumHomes()", "d0ea7808b429a35ea3f76637de8217a90d2bf5d192be1888d30d7e05f45b8a5b" },
            { "SC4Evolution.Simulation|public instance System.Int64[] SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot::get_StockQ()", "2632928b5475c0d75de0dc70356b2e785bc893bef67ecce876a639e8fa21d79b" },
            { "SC4Evolution.Simulation|public instance System.Single SC4Evolution.Simulation.DemandLink::get_Factor()", "c8b66a07e8ba317b2c6817adc4a6b3d9e5799e1b7543aa553e7aa978b70ca69d" },
            { "SC4Evolution.Simulation|public instance System.Single SC4Evolution.Simulation.ResidentialCapacityWindow::get_OpeningRaw()", "7de893279d1bc9d584dcb802f6f3876dc3bb3288cdeabb8b55ad7144af739e7e" },
            { "SC4Evolution.Simulation|public instance System.Single SC4Evolution.Simulation.ResidentialCapacityWindow::get_Overbuild()", "ea098a991d2261e8deecf2cbe7cd806f0854f29e7de9d360eb51a52cfac67e4c" },
            { "SC4Evolution.Simulation|public instance System.String SC4Evolution.Simulation.ResidentialLotIdentity::get_PrefabName()", "401419089063409a70c784e7cf2a0f06bd03cf1ceaa1ae66fc25e13174dfe676" },
            { "SC4Evolution.Simulation|public instance System.UInt16 SC4Evolution.Simulation.ResidentialLotIdentity::get_BuildingId()", "0bcdd48531d238da13af2e5e52eeeda5a192e6602a1dd0138f546370d38a4589" },
            { "SC4Evolution.Simulation|public instance System.UInt32 SC4Evolution.Simulation.DemandLink::get_TargetKey()", "bf4f7eaa588ad856693865e0ea9e80ccd2eb4ceafe200c19351cc932db5ed90f" },
            { "SC4Evolution.Simulation|public instance System.UInt32 SC4Evolution.Simulation.ResidentialClassLedger::get_PolicyVersion()", "68d73edf55154cfac800f7cb47d49261923e528154baa6e2ae97e23568dc8789" },
            { "SC4Evolution.Simulation|public instance System.UInt32 SC4Evolution.Simulation.ResidentialLotIdentity::get_NativeBuildIndex()", "79b890392e287fcb268811fcf2d36c1c72e2045e1648c9d119298d3254c9e120" },
            { "SC4Evolution.Simulation|public instance System.UInt64 SC4Evolution.Simulation.ResidentialCapacityRecord::get_Generation()", "0e9ddaec0a52ca1b539f784d0951fe94f30b14caf729cb37f0d68e86906f3a75" },
            { "SC4Evolution.Simulation|public instance System.UInt64 SC4Evolution.Simulation.ResidentialClassLedger::get_NextGeneration()", "1cf0180b1fd5bd2e82fb4a6d5ce6c3878a774e0d4ad120b4ca2793f300c78f33" },
            { "SC4Evolution.Simulation|public instance System.UInt64 SC4Evolution.Simulation.ResidentialLotRecord::get_Generation()", "f8b8c7abbd1ce8aa1cbc54029789c62ffb8b31362459b5ddb43f4543528052ac" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.DemandGraph::QueueSupplyChange(System.UInt32,System.Single)", "d4a51eaececd3f2b043d52b66560987aa9363b90254c94f08ea7ff03b487d4ea" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.DemandPropagationResult::.ctor(System.Boolean,System.Int32)", "8e31b12080de36d649ae05854350744c05f064a95751efa2365b9af47a33344e" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialCapacityRecord::.ctor(System.Guid,SC4Evolution.Simulation.ResidentialLotIdentity,System.UInt64,SC4Evolution.Simulation.ResidentialClass,SC4Evolution.Simulation.ResidentialNominalCategory,System.Int32,System.Int64,SC4Evolution.Simulation.ResidentialCapacityPhase,SC4Evolution.Simulation.ClassOrigin)", "dccce9c131f12ca2dd73d9e865de568298dab7530a786396a6f7692368dbc860" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialCapacityWindow::.ctor(SC4Evolution.Simulation.ResidentialClass,System.Int64,SC4Evolution.Simulation.ResidentialCapacityWindowPhase,System.Single,System.Single,System.Int64,System.Int64,System.Int64,System.Int64,System.Int64)", "7b2b35cf6e411dd93f3a7917b5d0a0b48e7a2212acee424e18bcab49f34bba90" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialClassLedger::.ctor(System.Guid,System.UInt32,System.Byte[])", "60153730c9044c649730e89653dc1e15b492293b5988f20f127e3e04d2e75259" },
            { "SC4Evolution.Simulation|public instance System.Void SC4Evolution.Simulation.ResidentialLotIdentity::.ctor(System.UInt16,System.UInt32,System.Int32,System.Int32,System.String,System.Int32,System.Int32,System.Byte,System.Byte)", "f418b2447ee8d2018c1fe746c665b783be63b18e7b5fe6ff4753f79a0d9ffa98" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Simulation.ResidentialClassLedgerCodec::Read(System.IO.Stream)", "04d40e8b6061c6664278cb347fe2e1da13c319783fbaa57018166cce83222829" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Simulation.ResidentialSelectionPolicy::MigrateKnownNativeSafetyV1ToWaterV1(SC4Evolution.Simulation.ResidentialClassLedger,SC4Evolution.Simulation.ResidentialWaterProfile)", "f31b26aefffeaf719c3d05177dd59094e94b5b2e1d4fa32016c414463007d9d3" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Simulation.ResidentialSelectionPolicy::MigrateKnownToRawCapacityV1(SC4Evolution.Simulation.ResidentialClassLedger,SC4Evolution.Simulation.ResidentialWaterProfile)", "d3adcbb277cc012334f563c76980046b13fab3c67a78ad59542ee8105d686f6f" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialHomeCountBounds SC4Evolution.Simulation.ResidentialNominalCapacity::GetHomeCountBounds(SC4Evolution.Simulation.ResidentialNominalCategory,System.Int32,System.Int32,System.Int32)", "2eb03a16f56e64a2c5b7ced0271cce840874ee72543ee15624a6264f609f5a10" },
            { "SC4Evolution.Simulation|public static SC4Evolution.Simulation.ResidentialSelectionPolicyId SC4Evolution.Simulation.ResidentialSelectionPolicy::Identify(SC4Evolution.Simulation.ResidentialClassLedger)", "e0e388794207096a2630a79c394ec7e2ac797f18d3ae23d609fa64025f97b36d" },
            { "SC4Evolution.Simulation|public static System.Byte[] SC4Evolution.Simulation.ResidentialSelectionPolicy::PolicyHash(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "7fd5b21f678ab6afdf33685a539acabccc199e73ccf6b32d9dc423eef9e7b69b" },
            { "SC4Evolution.Simulation|public static System.Int64 SC4Evolution.Simulation.ResidentialNominalCapacity::ComputeNominalQ(SC4Evolution.Simulation.ResidentialNominalCategory,System.Int64)", "62149fcbdab2ed7950c96ba1e9fb57d82df432278161751819cb7599b9a4c77c" },
            { "SC4Evolution.Simulation|public static System.Int64 SC4Evolution.Simulation.ResidentialNominalCapacity::GetQuartersPerHome(SC4Evolution.Simulation.ResidentialNominalCategory)", "0d67a51b6018f1c15fccfc3aa3b3a23200968df877b036241bb1320204f963b6" },
            { "SC4Evolution.Simulation|public static System.String SC4Evolution.Simulation.ResidentialSelectionPolicy::Name(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "6565bf609f7f2086e32286148dea5cb2c6dd6f26580213caa20ed81970642451" },
            { "SC4Evolution.Simulation|public static System.UInt32 SC4Evolution.Simulation.ResidentialSelectionPolicy::Version(SC4Evolution.Simulation.ResidentialSelectionPolicyId)", "9b2472926d70737aa7fd113790413e74fc86d1f1e82b6ed12e7d0a45e54533e2" },
            { "SC4Evolution.Simulation|public static System.Void SC4Evolution.Simulation.ResidentialSelectionPolicy::ValidateWaterProfile(SC4Evolution.Simulation.ResidentialWaterProfile)", "e14f0787aa88fc30d724898f95150f29d6f5cb8552699fe2d464b1453ede8913" },
            { "SC4Evolution.Simulation|public static System.Void SC4Evolution.Simulation.ResidentialWaterProfileCodec::Write(System.IO.Stream,SC4Evolution.Simulation.ResidentialWaterProfile)", "e5b802ce972e14547cfd703ba6fd7997415ac01f21970c7d4d02f872d3d93e9a" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityAttemptDecision SC4Evolution.Mod.Economy.ResidentialCapacityAttempt::get_Decision()", "4e80ff636e149051b29230629b61183964ca438cba70f236c6c2d0d64defc303" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityBridge SC4Evolution.Mod.Economy.EconomicBridge::get_Capacity()", "8df29e9d6a57be35ce4d7621d0986c21bf5e9f3d4c26b1a8564f6ef7e3521b02" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityMutation SC4Evolution.Mod.Economy.ResidentialCapacityBridge::BeginCreateMutation(SC4Evolution.Mod.Economy.ResidentialCapacityAttempt)", "8bb8603b2c39cab0fcde35cc44df40adacfe0b89cbe048bc21c273d391cdc5ca" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityMutation SC4Evolution.Mod.Economy.ResidentialCapacityBridge::BeginMutation(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationKind)", "5bcd6558f8ad046aeed518211d4e6e53cfba38449bc026c8cc6642b916c454d2" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityObservation SC4Evolution.Mod.Economy.ResidentialCapacityObserver\u002BReading::Result(SC4Evolution.Mod.Economy.ResidentialCapacityObservationReason,System.String)", "a39306c08cb1941540ef42e9258baea40156dfd910e523b56147bc9a8a6d78be" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityObservationPhase SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_Phase()", "f2078c4e440838dbdc7ee57f5aed763684f7d5fdbd100563befe53fddc800411" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityObservationReason SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_Reason()", "4dc02216838722fbde055465290e3b0ccb80b60184339e33e2579fcb0a17c7e9" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialCapacityObservationStatus SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_Status()", "1ec61f332ccdb6417eca2ecfc308117e4f2c22988c789fbf2de045838e327957" },
            { "SC4Evolution|other instance SC4Evolution.Mod.Economy.ResidentialClassBridge SC4Evolution.Mod.Economy.EconomicBridge::get_Classes()", "d4e9ef007801a55fa1a426954a17b9c73225c6d1c3771cc37cca04cb08f767b2" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_Identity()", "557e532f5682e9a6c5c4f141c080440b6b8c7f859b7fb06742be014c7205de0b" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialLotIdentity SC4Evolution.Mod.Economy.ResidentialClassBridge::BeforeUpgrade(System.UInt16)", "1c02d9c5f4756f4ba7cc55f45b52748f01a10f71435b039488b0115e16e6af81" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialLotRecord SC4Evolution.Mod.Economy.ResidentialClassBridge::ImportObserved(SC4Evolution.Simulation.ResidentialLotIdentity)", "ea40657eee7a44f4341fae607480547b3110e0644b08fe48edec04090fb6341f" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialNominalCategory SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_Category()", "30254c558cea9f8c95ff9697b995395192fe8ca8534ae4fe87ffb457318111a8" },
            { "SC4Evolution|other instance SC4Evolution.Simulation.ResidentialWaterProfile SC4Evolution.Mod.Economy.ResidentialWaterObserver::get_Profile()", "4f4a49c139d02bedc9e7a1cc9644571ace625a8efb66cbeb7361df4e26d7dd72" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.EconomicBridge::PublishCapacityMutation()", "24ae733e0b99d2acdf6145cb0d07ef220a7f2ab51a021ec7e9b927b0cf17ba25" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::AcknowledgeGraph()", "8f9c4ce4eeeb618f692025d682906ecc3db27b733bbcf1306f024d750fdf5eb2" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_HasRetainedState()", "afcdff4b783c90d1d83b2c0352ad070f44b4d84cbe6aed43cff98715d4dac053" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_ModeRequested()", "1420ccd8b7eedeb7a0572941d05496b1cb5970f6ea3dd15f1d4b37eb473a2778" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_Ready()", "72582601706ca6aa7b180927907fc808b0ec8e11b7b625d386248ad239589126" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryRecord(System.UInt16,SC4Evolution.Simulation.ResidentialLotRecord\u0026)", "898aa05ee11b46e1216ddfacb88e64fba86a98104f80085f80a6947e89ebd8ec" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::get_CapacityPolicyBound()", "d7e84c739b6ee42f69bcfb17c6f93a04eaaff2d3ba557257106d993a62ce43f2" },
            { "SC4Evolution|other instance System.Boolean SC4Evolution.Mod.Economy.ResidentialWaterObserver::get_IsValid()", "76f2c54738cca8366e78d21e45ec38714e3b4563bcc8669b5e033f8f6246005f" },
            { "SC4Evolution|other instance System.Int32 SC4Evolution.Mod.Economy.EconomicBridge::get_DemandContextSession()", "49150eeceeba25af406d380fb2155ff0307cade33ba406c5535a82e976d48e97" },
            { "SC4Evolution|other instance System.Int32 SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_SessionEpoch()", "d4c8dd5b2a849962643f8a985a19c4a40da5d222b00e5b288f9afc6811f80847" },
            { "SC4Evolution|other instance System.Int32 SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_NativeLevel()", "f52a869844c317e2b3444defd9ebbbdb872708dc3c1637082163c5870a0bafc1" },
            { "SC4Evolution|other instance System.Int32 SC4Evolution.Mod.Economy.ResidentialCapacityObservation::get_ObservedHomeUnits()", "b0e858a1335a6273e6bf96288ba306eac4b9173b98fdbf5005f21da5c9a8eaf3" },
            { "SC4Evolution|other instance System.Int64[] SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_SupplyQ()", "d2feb793b5a67fc371cfb9c049a3768ba38e27a75278032854d8e82fcb061b0b" },
            { "SC4Evolution|other instance System.Object SC4Evolution.Mod.Economy.EconomicBridge::get_CapacitySync()", "f4c34683c19af90567a128af60cfbafbca129c941ac3e2cae666bb9bffc615f0" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::BeforeRelease(System.UInt16)", "aab619e6f88b033614e43f6059622adc320d3786ff79711bc2460fbbd4de2582" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::Cancel(SC4Evolution.Mod.Economy.ResidentialCapacityAttempt)", "6aed4766e5e34a14878f890d9030aa48963c382f32e04add1146521bd9105846" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::ConfirmHookSet(System.Int32,System.Boolean,System.String)", "36fa2525a6cf245c4c98e0ddc61bac9686cd72edf7038c1479e8b54f36910071" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::EndIntent(SC4Evolution.Mod.Economy.ResidentialCapacityIntent)", "0a0848fe7c7dd0a56f73b854f7606ae49066cf84b3b84c41082884d9063d26ed" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::EndMutation(SC4Evolution.Mod.Economy.ResidentialCapacityMutation,System.UInt16,System.Exception)", "c35df48f656fa7cdd2c4513dc1039201b773dff9c459fef329962135c8eeeadf" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::Suspend(System.String)", "c555caf95dc7b8c4a8f92a1d2ef16f5951d3a1376c9a422c0dc155f9915de459" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutation::.ctor(SC4Evolution.Mod.Economy.ResidentialCapacityBridge,System.Int32,System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationKind,System.Boolean)", "4ca6f6759702192bb15f0134e7acea4d6309384382fccba225af54a5343dbfee" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::.ctor(SC4Evolution.Mod.Economy.ResidentialCapacityObservationStatus,SC4Evolution.Mod.Economy.ResidentialCapacityObservationReason,System.String,SC4Evolution.Mod.Economy.ResidentialCapacityObservationPhase,System.UInt16,System.UInt32,SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialNominalCategory,System.Int32,System.Int32,System.Int32,System.Int32,System.Int64)", "030b3c6d3bff19abb0ae9306da8be3bc31f0ad32e49f78f146d5ac5fdef41587" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObserver\u002BReading::.ctor(System.UInt16)", "a50445fe385b93c88802f45fabbae18b0ac09038b680017fa8fc05d7cc18700e" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::.ctor(System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.String,SC4Evolution.Simulation.ResidentialCapacityLedgerSnapshot,System.Int64,System.Int32,System.Int64,System.Int64,System.Int64,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32)", "54903b1ed65f127e8433853301f679e7cc2f0ed218f9f98a37f7973226d18003" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)", "ace34c9ae8a83d1a72d995e3c373991ca88b25823b5e694dcba7f28eab06fe4d" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Disable(System.String)", "66b1ca2f4645d440b5c7cc682ec411f54d3bb1a6a34c25d95084487aadc4a938" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Mirror(Citizen\u0026)", "91c2ec98a6e1d5abeb573e0089492d2eba97952361e8a1024a91bc2517766e9c" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::MirrorUnit(CitizenUnit\u0026)", "9fd5e42272ed4d1168118e683625a79efc14be930246340181fb246ee7156eb3" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RejectBinding(System.String)", "dbc8806aa20936367a5e49f6676a60d9c8a2afd2d997c21636e59ef889ace791" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::Released(System.UInt16)", "3e7b2dcbff7400fcbafc87ebf85de562057a980ef57e702d7701053ac47ed981" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::SetCapacityEffectsReady(System.Boolean,System.Boolean)", "915a6031191163d9fbb5c5e3c8216cdddbb98775342043a0720066b93305913a" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::.ctor(System.Boolean,System.Int32,System.Int32,System.Int32,System.String,System.String,System.String,System.String,SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus)", "84b6bdb9a0d6d4e90edb15118a8ba08196592c6c122ffbaf32416c1ee2789837" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::.ctor(System.UInt64[],SC4Evolution.Mod.Economy.ResidentialLocalDecision)", "edbb14bec43cfb9f3cea997994239d2b3a5caf4c91392e7e59153c08dd775b0a" },
            { "SC4Evolution|other instance System.Void SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::.ctor(SC4Evolution.Mod.Economy.CityCensus,SC4Evolution.Simulation.DemandNodeState[],System.Boolean,System.Boolean,System.Int32)", "df538c78e4fd1e85d62aa1ab2e8d7e8b155ee673d2536e91d74fff8192cbcc60" },
            { "SC4Evolution|other static SC4Evolution.Mod.Economy.EconomicBridge SC4Evolution.Mod.Economy.EconomicDemandExtension::get_Active()", "bd8938feecb7675ed91d2b3b0002bb124270a0844ffa0fa9fa8c4ea3ffc0ee66" },
            { "SC4Evolution|other static SC4Evolution.Mod.Economy.ResidentialCapacityAttempt SC4Evolution.Mod.Economy.ResidentialCapacityCreationContext::Current(SC4Evolution.Mod.Economy.EconomicBridge,SC4Evolution.Mod.Economy.ResidentialCapacityBridge)", "dca466383c337df334bf7e88ab0bc31a27063ef59257f8d6b0e57a464a844e55" },
            { "SC4Evolution|other static SC4Evolution.Mod.Economy.ResidentialCapacityObservation SC4Evolution.Mod.Economy.ResidentialCapacityObserver::Observe(System.UInt16,Building\u0026)", "c6117836bf3158ddc6c026eef1fc8642a1ea438c31d24732b3522a3b6951fed1" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.NewLotGrowthGate::NativeBuildingExists(BuildingManager,System.UInt16)", "f1b3f0e3cf722285925899215fe28a96cf4f3e313ce9c56b75757d0579ead663" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::OnSimulationThread()", "6638d3239af5f114203e5f17681d4de5c928d33a5959096cd76285e8ba604d3e" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryIdentity(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity\u0026)", "6c4a59c531715f274621f460dcf424cd6325ec633a6c22f8b3f2d6c1b2c79b5d" },
            { "SC4Evolution|other static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryMirrorValue(System.Boolean,Citizen\u002BWealth,SC4Evolution.Simulation.ResidentialClass,ItemClass\u002BLevel,Citizen\u002BWealth\u0026)", "d637276ca6ba27a49e15e616f4cc0c9f35dd7a9896515d7530afb11441c7cce9" },
            { "SC4Evolution|other static System.Int32 SC4Evolution.Mod.Economy.ResidentialClassBridge::FloatBits(System.Single)", "05a9845441a99483d378bed5a457a444286d33135464a60b6b507864875864fe" },
            { "SC4Evolution|other static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::DisarmCapacity(SC4Evolution.Mod.Economy.ResidentialCapacityBridge,System.String)", "fa45c53f0e384c65e0794128e8ed365dc81579180cf8d8b8159b58dfc37325f7" },
            { "SC4Evolution|other static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::Released(System.UInt16)", "604ac498b37d937a5d96156e53f75fecaf1985cd688f66180c40a37d2d81a5f2" },
            { "SC4Evolution|private instance SC4Evolution.Mod.Economy.ResidentialCapacityMutation SC4Evolution.Mod.Economy.ResidentialCapacityBridge::BeginMutationCore(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationKind,System.Boolean)", "1ba3b8fd059e074386146d7c2a180f5b72d8ca8b30da7140677b18e31e0aea05" },
            { "SC4Evolution|private instance SC4Evolution.Simulation.ResidentialCapacityRecord SC4Evolution.Mod.Economy.ResidentialCapacityBridge::MakeRecord(SC4Evolution.Simulation.ResidentialLotRecord,SC4Evolution.Mod.Economy.ResidentialCapacityObservation)", "bff7261f94eca0afb80f6657b340c2bc6d18c9bd01d0b487caf423b75a923b84" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::AdmissionScopesOnly()", "1bf1f168bbac5302871670c868c770d5c3f72dce9cb73b9decfc549fd459729f" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::HasOtherMutation(System.UInt16)", "282cb6f847040baf7dc63c328e0542706053eefaa5f9c3b171313cb9c6b0f3b9" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::Owns(SC4Evolution.Mod.Economy.ResidentialCapacityIntent)", "82cfa594ff65d44ad7866ad5cf5e15c1562dee1fdbd4649d3a6b083e2eb36ff0" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::Revalidate()", "5cb8ff6a2d13bb536a642248c0025d69aa40c121fa9d24d2b230ea230faf6f7a" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_CanControl()", "5f5eb8f005dccaf272e6608d682340fe8bfba0d8d09369b268b9fc99400db8c6" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_Current()", "90ed232abe02905c7e6f5d95afb2a1be1ad061820341a2e392754e1ca15c7ad7" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::get_HasAuthorityPrerequisites()", "46512b075c2c69a76b2c9887a495d0a15f7bb2b3dd25dadad260bd1382343524" },
            { "SC4Evolution|private instance System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::TryRecordLocked(System.UInt16,SC4Evolution.Simulation.ResidentialLotRecord\u0026)", "edce31674a3d9fcc8367b64f36946fa658ab1164e56547374f8d8e436fea5184" },
            { "SC4Evolution|private instance System.String SC4Evolution.Mod.Economy.ResidentialCapacityBridge::StatusReason(System.Boolean)", "371d73b222615809cd96970736fc39f3f8b2a7a8a1bb434ed1e3b00dbc1d0104" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.EconomicBridge::Supply(System.UInt32,System.Single)", "1d59e62e6c465e9788ab68e1792690c1ac6deb71089f040469d26aedc180aa32" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::ObserveCore(System.UInt16,System.Boolean)", "0f5ad3f28b30f0a98b6888049624ccae7fba1c2e2f850c5681d422e21d6d6c50" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityBridge::Publish()", "4c29344c058a036ef466affec4225a7e76a58a0ed51397d052e72ca5711e4a1f" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_BuildingId(System.UInt16)", "a95938300def62393659fe1a8e217e85bb883e5c075e816ce8158f695efd57e0" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_CapacityQ(System.Int64)", "8128de56107b4d5cd90988d77fdccd210dd02cb3d43a7d1e12f9806e8b641748" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Category(SC4Evolution.Simulation.ResidentialNominalCategory)", "8e1a5577c193f0e4a9b79998f5294467fb0ca56beed7d53ab367518692b8747e" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Detail(System.String)", "ff2170fcd3b87bddb1244f24217c75cf2310253a474fc3f751b1953a103b32b2" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_ExpectedHomeUnits(System.Int32)", "e16b677ce95751550386aba6dc010e6ab5971281401d7f396d46369c0c65f156" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Frame(System.UInt32)", "f96bd3375f5321c7d1c11ca8ca184414d7a7bd6c4490cfce3d4d0e3f8160003f" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Identity(SC4Evolution.Simulation.ResidentialLotIdentity)", "5310abb55b2c2d5fdd4c7908bc7b74c6a8d2b4ca750ec7e6546cd17e2263d304" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_InspectedUnits(System.Int32)", "64cceb49d967a79273c604c75e3b1ddd031656b5710530b0b763d87f0600cdca" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_NativeLevel(System.Int32)", "449830201ba71361cd098ac1b435a3580eba8f7a4fbb1e217f4b93354b5a9998" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_ObservedHomeUnits(System.Int32)", "737c8eb6b0ed8a80cdd6e3c2ed9dc27ace97d092260cbf1bf84d4b5459d5e50c" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Phase(SC4Evolution.Mod.Economy.ResidentialCapacityObservationPhase)", "f5c6a7c70a20c5fab3dd9e4944473c38b32ca5809591c0bb315e05361bbd66b5" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Reason(SC4Evolution.Mod.Economy.ResidentialCapacityObservationReason)", "59af89ca3b016a0d52d079e63f575da1065cefbfd5694187845e1212cf4b2d32" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityObservation::set_Status(SC4Evolution.Mod.Economy.ResidentialCapacityObservationStatus)", "9b14e96c8b87f0f2847ba6997c4742f5a47ccb0944fd6d48a4996de60e591d52" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_CommittedCreations(System.Int64)", "e32608465f7a6eaad334566daa91e90a2e6b1ed8cda18ba24bce4b618078ec2d" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_CompatibilityKnown(System.Boolean)", "d31f39e2e95070ca4d212a6945559792c965e12ec191354c428a5f42e89288df" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_ControlledIntents(System.Int64)", "e21cd87c960125e953d86810a63172b0f1d219977350f49da97a364746c21ea9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_Day(System.Int64)", "bde298baf775d63748d1234f15d291c4160d0f05b3e31aa639559712ffdcb542" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_ExternalRevision(System.Int64)", "1aeced9afc84dc1694b1247cc04bac88a733baed32a0643c8d6255e2a88118ff" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_HooksReady(System.Boolean)", "6ed24f8ec0a5e757826f5965e2fa55fa2e17f917456ee374facc8831086bafd4" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_LastNativeRoll(System.Int32)", "8851cfa2420e593441cce53f0f5f537dc2f8a9e95c4184c0811fc286c8b4ac19" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_LastNativeThreshold(System.Int32)", "a02a16790b551b4b7772662941df0a397612ead88b9a31e8f004da285bed2f85" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_ModeRequested(System.Boolean)", "cd025294a852e17a0d0086a92b86411f94d70058403fc8138d2401be6230b462" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_PendingMutations(System.Int32)", "4e1af79334e154332fd884d400acefe24345e7a5f49ede00e8451c3dca7346eb" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_PendingReservations(System.Int32)", "171c274dbc57190a36c5634690b73e57fa7065fcc3e3171d15e2b8940b1393cc" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_Ready(System.Boolean)", "b89d2577babc6ed700c3a60c91beedd140740f91f1a1469d2fe30dccd1574b21" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_RejectedIntents(System.Int64)", "589853fc881fcffa2338977629cc738907253d7132f53df26b3b1a67d6602f74" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_ReservedAttempts(System.Int64)", "e55815309179b7e9da7be441418a01f734c61400472024b7f201ee7135029b8e" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_Status(System.String)", "d5f0f1e7812130a1ad547cfb49eaabbb2b22272f11e9fb0ddfc92da1dbef83ce" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_StockRevision(System.Int64)", "9a4329d81828e777eac3aa86c88f6ea2a6ed4fc1046f178ec91c88eef204029a" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_UnknownBuildings(System.Int32)", "14e28abc8292c231e5a540086b817b28f22b9c4a11f823d12c0e0cca601e9b29" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_UnresolvedCreations(System.Int32)", "29cfbc2126cd28a26d04b99cfb66de69cccbc105578f9007f575b60039937e32" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityStatus::set_UnresolvedMutations(System.Int32)", "a0d08ffa8f8dc0eb7c84a1ba4f3170b381c9dbeb97fb4c5ed0c51fe48d7dbbb5" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::BindLocked()", "5a887a0e3542edc954a78dfac62980178f02b955b70c4418ff748f17240c0da5" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::MirrorCitizenLocked(Citizen\u0026,System.Boolean)", "a5c6176b47d3bfa4cddb6e930f776e1e748a6d52276060c27589074df07aadf9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::PublishLocked()", "d11c04349db881eb247122305984721f546a8c3e07e3223b6fa4d8db1c3e16cb" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::QueueHomeLocked(System.UInt16)", "96142d7d3a1eee22a168b2cbc55ec190667be64f648f1fc36e1a08038a024261" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RecountOriginsLocked()", "361b3f0ad9d69614cd87323f356ea017e68500cc05278dec3a0e61dbfe6d3416" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::RemoveRecordLocked(System.UInt16)", "8a30d1540465a3305f1fdee50b29ded0052e91e49bea1f2ffe924f4d3f322cc9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::ReplaceLedgerLocked(SC4Evolution.Simulation.ResidentialClassLedger)", "62932e0f026165394d43201a5d4a723b9223a2ae12a225040f3351c9cfc90125" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_ConfiguredPolicy(System.String)", "79bef1781c0af716edd3132837875b5f521a0c3ca9455daf430074e240b17a09" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_EffectivePolicy(System.String)", "718bbf3a464217683b3d2842a6a1ab3878c529181652b2b21568cce21a494a42" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Enabled(System.Boolean)", "5abcae121fc43ad56fe2b397de215731a1d875d4a19f44e89e84603d57765348" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_ImportedCount(System.Int32)", "656806dc1c50380b49739e044144ad80567ae04685d2d3fbdc201b01e940c827" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_LocalDecisions(SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus)", "658bff814ca5e7619d1ff016a902e1a1c387235294ea20d2baf939366d5a0862" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Policy(System.String)", "01055602657c1b8f78217084a40fdff7686721c6c29f2042f0706dcc4646d304" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_RetainedPolicy(System.String)", "d83fb85aa0436fb00aa4bed986fe3eaf6beaf752a421526eda69249e8b93599b" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_SelectedCount(System.Int32)", "29ad31e0cafc4e12531308991b4daf27f9aba183baf946f199101f7c9c8f76e1" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_Status(System.String)", "fa5073ac770bf06acf04da4bf2779204432fba4d1edfd48d57fd15e3dd20c0f0" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialClassStatus::set_UnboundResidents(System.Int32)", "980584c093e658ba27f28dac5e1039a9c68c2939373608486a00abbf8364fcbf" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_ControlledRejections(System.UInt64)", "8cdd5c70a92d2124e84e3aa5bec0b61fdecf795dc638df4c9e0ff4c56f39498f" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_HighChoices(System.UInt64)", "40f73de0d5164b936b047b7ada6cc2006d8afba7af7a459d3d5a1f6a796b73d4" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_HighVetoes(System.UInt64)", "c590ae812140f5829c7ae649fc1ea4678a3a3b448caccbc682f046e39d9ed10e" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_LastDecision(SC4Evolution.Mod.Economy.ResidentialLocalDecision)", "154cc60042a6e237ebfcd089c1fc0d72fc4e380a354a00fb952f6a36e8073a2b" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_LowChoices(System.UInt64)", "67f47375c38f7cbf359ebc2a2bd8c96d7c1c1faacc78f4c742a8888aa7d77559" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_MediumChoices(System.UInt64)", "2501e70b46664c5f55876ca0da21772a999b96ed261ae7ec12aeed2d26916256" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_NativeFallbacks(System.UInt64)", "45df79a56e553f3863ed1587d93f6472e008d14704b08a7e1c8da477159003b3" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Served(System.UInt64)", "2f4e552c90d05497003d89524725a6c7e88bae426af26aef46ba92ed5a15d1fe" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Unknown(System.UInt64)", "b32cdaa8d0c9088c833647b9a981a27d4e62d7b0c9c0fcd76cbd99802ccc6e25" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_UnknownDomain(System.UInt64)", "fb19fad549fe3ca5df561bfb591aa4ecdd331a753ed9518647484c7db938ce15" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.ResidentialLocalDecisionStatus::set_Unserved(System.UInt64)", "3e3b640dd3f16423d7b747c9909b21df98c2346f620c5fa6ff9587d093442bba" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::set_Census(SC4Evolution.Mod.Economy.CityCensus)", "642bf5b6fe68a52776b530b56df8194f171d9a49202242d3074ad1743d977b73" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::set_Converged(System.Boolean)", "59e4309596a97f4e48be07491ec1544a365692527d313a9df7741fe1aaef641c" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::set_GameplayEnabled(System.Boolean)", "ffa39ccb45135075398817ffb16d6a39fe5b25f91e318e3bc29efd7d300edec9" },
            { "SC4Evolution|private instance System.Void SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::set_PropagationPasses(System.Int32)", "79f165637a1a3fc0863b8c7e1601f71e77a24253cc3586911c3f842303ced0da" },
            { "SC4Evolution|private static HarmonyLib.CodeInstruction SC4Evolution.Mod.Economy.ResidentialClassPatches::UniqueBranchTarget(System.Collections.Generic.List\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]],System.Object)", "1e2a2f1061ad613ef88639272c732cdcb0a7b88e9c3da787748034e1b92088da" },
            { "SC4Evolution|private static SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::Capture(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationKind)", "4b42577fa1736d950b361ef893c85a6cdd957f3d6953a3a43098c158a068d87b" },
            { "SC4Evolution|private static SC4Evolution.Mod.Economy.ResidentialCapacityObservationReason SC4Evolution.Mod.Economy.ResidentialCapacityObserver::InspectChain(CitizenUnit[],Citizen[],System.UInt16,System.UInt32,System.Int32,System.Int32\u0026,System.Int32\u0026)", "94c1a29da5e283ecb05709e9527b539879fd1a56b1cc02d1700a150ae9148dbe" },
            { "SC4Evolution|private static SC4Evolution.Mod.Economy.ResidentialClassBridge SC4Evolution.Mod.Economy.ResidentialClassPatches::Active()", "be732c311d4771e2854268d732d4df8e9c6ae776c03a1804ff38a4b80282d608" },
            { "SC4Evolution|private static SC4Evolution.Simulation.ResidentialCapacityRecord SC4Evolution.Mod.Economy.ResidentialCapacityBridge::WithPhase(SC4Evolution.Simulation.ResidentialCapacityRecord,SC4Evolution.Simulation.ResidentialCapacityPhase)", "2916e482e0caa545604a3cf9eb374d596df579e6050725462173449b4122c2bc" },
            { "SC4Evolution|private static SC4Evolution.Simulation.ResidentialClass SC4Evolution.Mod.Economy.ResidentialClassBridge::Bootstrap(ItemClass\u002BLevel)", "cb1c379bc16da59fcc76095779680dc31f6db7d6faa751944afcc76d8c443a33" },
            { "SC4Evolution|private static SC4Evolution.Simulation.ResidentialClassLedger SC4Evolution.Mod.Economy.ResidentialClassBridge::PreparePolicyLedger(SC4Evolution.Simulation.ResidentialClassLedger,System.Boolean,System.Boolean,SC4Evolution.Simulation.ResidentialSelectionPolicyId,SC4Evolution.Simulation.ResidentialWaterProfile)", "6c4eb42bd040bf98a88f4639167a558b362a76b141324b3736ee918baa04584f" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.NewLotGrowthGate::Calls(HarmonyLib.CodeInstruction,System.Reflection.MethodInfo)", "c8bc27a20d00f9b60c22908a5b1d60570c760e854022ff8c365e5a6142d01e4b" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.NewLotGrowthGate::Integer(HarmonyLib.CodeInstruction,System.Int32)", "017640e81026208a5d2dcfda0275e79ae33820c79282fb0642ed2450778c7cdf" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::SameCapacity(SC4Evolution.Simulation.ResidentialCapacityRecord,SC4Evolution.Simulation.ResidentialCapacityRecord)", "32bbce1098601fa6a92b0271493c7f1c1da50d003aac16caaba124aee097327b" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityBridge::SameIdentity(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotIdentity)", "0c3d59c8013c28d31484b3868d0bffce51c618bfe82dd7b42865f7d07e05ea26" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityObserver::CoherentPrefabBinding(ResidentialBuildingAI,BuildingInfo)", "88cb9135c5a55e6cd1823f95e014893d90be23ccc06e185c2ca34ed4ecf6f9f6" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityObserver::SameBuilding(Building,Building)", "ca68c0fae36c1c6a32f06571011f1a83601acc5cb0e86c02caadf6106d3cb253" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityObserver::SameIdentity(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotIdentity)", "7e174b205fadb782f0bb3864cf50c58e56eaa4331fb6a1c6370554af6209b26d" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::SameIdentity(SC4Evolution.Simulation.ResidentialLotIdentity,SC4Evolution.Simulation.ResidentialLotIdentity)", "9d21357f060cdcbf3ea80e8b3447d56bf94c0c89487da6185fca1c62b195aaee" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassBridge::SelectionPolicyMatches(SC4Evolution.Simulation.ResidentialClassLedger,System.Boolean,SC4Evolution.Simulation.ResidentialSelectionPolicyId,SC4Evolution.Simulation.ResidentialWaterProfile)", "e7fc8e98fc8c3035c3b4097329780e172dcc29ff253accbde19d3e432fcefa38" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassPatches::Calls(HarmonyLib.CodeInstruction,System.Reflection.MethodInfo)", "0be951fec7627e3d4c93e0ad2a68049fe184d656b8bb9fc9e709275dcace141e" },
            { "SC4Evolution|private static System.Boolean SC4Evolution.Mod.Economy.ResidentialClassPatches::SameBranchTarget(System.Collections.Generic.List\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]],HarmonyLib.CodeInstruction,HarmonyLib.CodeInstruction)", "42d7cb6c9451c20b312072ca22b1dee797a96b8ccb18e8d1392a72a1c0abe127" },
            { "SC4Evolution|private static System.Collections.Generic.IEnumerable\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]] SC4Evolution.Mod.Economy.NewLotGrowthGate::Transpile(System.Collections.Generic.IEnumerable\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]])", "89eae6751193a15dfa83933e938a5c26b82bad7796841b1bc96b9bb9cf067307" },
            { "SC4Evolution|private static System.Collections.Generic.IEnumerable\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]] SC4Evolution.Mod.Economy.ResidentialClassPatches::TranspileRedevelopment(System.Collections.Generic.IEnumerable\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]])", "3213fb503d493b3865d55ace96c588d025be7e99593fc85ff0df42d9fa668e00" },
            { "SC4Evolution|private static System.Exception SC4Evolution.Mod.Economy.NewLotGrowthGate::EndAttempt(System.Exception,SC4Evolution.Mod.Economy.NewLotGrowthGate\u002BGrowthAttempt)", "2851ce8d62d80bea7fee73bf6b2e445ecc0c385882bda2bbdde7ad120644fa43" },
            { "SC4Evolution|private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks::End(System.UInt16,System.Boolean,System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks\u002BState)", "ad34ec9cd86a91c8d6d0bb2d0c7bc36de78d69d7f91698ee404b71dc202ae4f5" },
            { "SC4Evolution|private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
            { "SC4Evolution|private static System.Exception SC4Evolution.Mod.Economy.ResidentialClassPatches::EndAttempt(System.Exception,SC4Evolution.Mod.Economy.ResidentialClassPatches\u002BAttempt)", "3544c76c0172e9734ad53895ed4002c47e8aaf31954223d2c112818c6868e39e" },
            { "SC4Evolution|private static System.Int32 SC4Evolution.Mod.Economy.NewLotGrowthGate::Local(HarmonyLib.CodeInstruction)", "27626c59b198139e371fb0c1743a37363e221d357acffb21bf9f7dd297cd4a48" },
            { "SC4Evolution|private static System.Int32 SC4Evolution.Mod.Economy.ResidentialCapacityObserver::Bits(System.Single)", "d97a14468be85d16422179c00d89db12e2da358e9971233818bf12b736e456ae" },
            { "SC4Evolution|private static System.Int32 SC4Evolution.Mod.Economy.ResidentialClassPatches::LocalIndex(HarmonyLib.CodeInstruction)", "598c50847d639e2725e9c8648445c293c899b0deb6dd02f1db01a654c944cf1e" },
            { "SC4Evolution|private static System.Single SC4Evolution.Mod.Economy.EconomicBridge::ExactNominalSupply(System.Int64)", "175541cd1a682857bb7ab95b720ddf26d8a6df295b2e976c05ef19dd630f70f1" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.EconomicBridge::.cctor()", "753720c77296e37e24222e7afd10ed12754d97812f93027bccf21c47a0808a43" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.EconomicDemandExtension::.cctor()", "fc65977d50345d9b7d9a28f9fd5430daa1e4242bb627a63f030739acf7d6d566" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate::.cctor()", "5f0e8a0228c80c6c44fff88a941289d8dd28d794577861491ce31339ffe30901" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate::BeginAttempt(System.UInt16,SC4Evolution.Mod.Economy.NewLotGrowthGate\u002BGrowthAttempt\u0026)", "a18051254bdedd8b196480452479f0b9ab1cdb9adf1625a84d3cc349b9ceee47" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate::Disarm(SC4Evolution.Mod.Economy.ResidentialCapacityBridge,System.String)", "2478fdc5698280be0f92336b2fb2f95cbcf8ea94e08dc6022cd895a4e24af744" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate::ValidateNativeDraw(System.Collections.Generic.List\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]],System.Int32)", "564cc1407a148764f3681e19006bfe3970d5f4a589e94bc9671103a69feef350" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks::Begin(BuildingManager,SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks\u002BState\u0026)", "d03765b8c04e92a52ae54a0523c449ccf17adfcb6622ae0f2735f1720852347b" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::Begin(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState\u0026)", "e310c461f3b980ec98a763c8255a9487e2a55927c6d593933292a558e1264913" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::BeginPhase(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState\u0026)", "d1c60999685e910bf6f8715614d63b515857a2c347778a2ee4eba5105d5bb88c" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::BeginRelease(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState\u0026)", "5b1e882f76f024909291667eb5e125cbc148efe7cb88eda5b0f5e8963508119d" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassBridge::.cctor()", "2d61042600f3792637cb2ceab4a076e0733937d252687557c996d2d92a3051b2" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::.cctor()", "1c4cd09ce0f60ee289060037618a0ec289ee0277ba729103546b726d48cb6672" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)", "0b731a787ee765dd69f33f084b10c6ab9243a86ebd82868265ef244f3bda6238" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::BeforeUpgrade(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity\u0026)", "e2f91b3281fb139d5fbf27a392a3c89544ee28fb77907ff4bdb2924d7dacaac0" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::BeginAttempt(System.UInt16,SC4Evolution.Mod.Economy.ResidentialClassPatches\u002BAttempt\u0026)", "cd4f773a741c614ca0d60dc24917492fd57bbd19a4b8bc885c63249064f49231" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::Created(System.Boolean,System.UInt16)", "50abdb847e1337207a203d2df8824411037cc952ae834b460e0059b830c5f78a" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeChanged(Citizen\u0026)", "6d95ff3420ad2a719e7d9a65195cef10befa6a63d4cf849e556cbe0b23253907" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeUnitChanged(CitizenUnit\u0026)", "506b0c6a5fdbb874e60a377b0d9c223a4d743122969b63b529278e8ec3b21af6" },
            { "SC4Evolution|private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::ValidateRedevelopmentDraw(System.Collections.Generic.List\u00601[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]],System.Int32,System.Int32)", "d832557ec18de01753e116ca2c85cbb9a8bdeef3dd0944c3344511d4fe757cab" },
            { "SC4Evolution|public instance SC4Evolution.Mod.Economy.CityCensus SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::get_Census()", "a9707ae5de1a80f597bc8da6ee136ba139cbb648d861d197a960269dd7367b83" },
            { "SC4Evolution|public instance System.Boolean SC4Evolution.Mod.Economy.ResidentialCapacityStatus::get_Ready()", "9388022596f4f88742d492c8a5a97f9ed599fc941b67dc9707b2798d508ff0e6" },
            { "SC4Evolution|public instance System.Boolean SC4Evolution.Mod.Economy.SegmentedDemandSnapshot::get_Converged()", "0c6b061d25a1caa3ca74623a730152670e3684629395b912cc8452c4980e9828" },
            { "SC4Evolution|public instance System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate\u002BGrowthAttempt::.ctor()", "631f57878993ad166c004d9cb782e2cd730e3cc2314a6f98f7543405a1f0edb6" },
            { "SC4Evolution|public instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks\u002BState::.ctor()", "d39e5c12fa265bd9996335814e1573f7a8fce55582789010591c5d893ce0ca4b" },
            { "SC4Evolution|public instance System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks\u002BState::.ctor()", "754bec7f153b78293ae433f05ad0ea71e00da5181e1bdcea4ee2365d91006b34" },
            { "SC4Evolution|public instance System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches\u002BAttempt::.ctor()", "4df5af1864d83801e9d66875fce7db6e9776fad49352bd49ee8d0ed426a99520" },
        };
        // target/kind/owner/assembly identity/hook signature/canonical hash.
        private static readonly string[,] Approved = new string[,] {
            { "public instance System.Void ZoneBlock::SimulationStep(System.UInt16)", "Prefixes", "SC4Evolution.NewLotGrowthGate.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.NewLotGrowthGate::BeginAttempt(System.UInt16,SC4Evolution.Mod.Economy.NewLotGrowthGate+GrowthAttempt&)", "a18051254bdedd8b196480452479f0b9ab1cdb9adf1625a84d3cc349b9ceee47" },
            { "public instance System.Void ZoneBlock::SimulationStep(System.UInt16)", "Transpilers", "SC4Evolution.NewLotGrowthGate.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Collections.Generic.IEnumerable`1[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]] SC4Evolution.Mod.Economy.NewLotGrowthGate::Transpile(System.Collections.Generic.IEnumerable`1[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]])", "89eae6751193a15dfa83933e938a5c26b82bad7796841b1bc96b9bb9cf067307" },
            { "public instance System.Void ZoneBlock::SimulationStep(System.UInt16)", "Finalizers", "SC4Evolution.NewLotGrowthGate.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.NewLotGrowthGate::EndAttempt(System.Exception,SC4Evolution.Mod.Economy.NewLotGrowthGate+GrowthAttempt)", "2851ce8d62d80bea7fee73bf6b2e445ecc0c385882bda2bbdde7ad120644fa43" },
            { "public instance System.Void PrivateBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "Prefixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::BeginAttempt(System.UInt16,SC4Evolution.Mod.Economy.ResidentialClassPatches+Attempt&)", "cd4f773a741c614ca0d60dc24917492fd57bbd19a4b8bc885c63249064f49231" },
            { "public instance System.Void PrivateBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "Transpilers", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Collections.Generic.IEnumerable`1[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]] SC4Evolution.Mod.Economy.ResidentialClassPatches::TranspileRedevelopment(System.Collections.Generic.IEnumerable`1[[HarmonyLib.CodeInstruction, CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null]])", "3213fb503d493b3865d55ace96c588d025be7e99593fc85ff0df42d9fa668e00" },
            { "public instance System.Void PrivateBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "Finalizers", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialClassPatches::EndAttempt(System.Exception,SC4Evolution.Mod.Economy.ResidentialClassPatches+Attempt)", "3544c76c0172e9734ad53895ed4002c47e8aaf31954223d2c112818c6868e39e" },
            { "public instance System.Boolean BuildingManager::CreateBuilding(System.UInt16&,ColossalFramework.Math.Randomizer&,BuildingInfo,UnityEngine.Vector3,System.Single,System.Int32,System.UInt32)", "Prefixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks::Begin(BuildingManager,SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks+State&)", "d03765b8c04e92a52ae54a0523c449ccf17adfcb6622ae0f2735f1720852347b" },
            { "public instance System.Boolean BuildingManager::CreateBuilding(System.UInt16&,ColossalFramework.Math.Randomizer&,BuildingInfo,UnityEngine.Vector3,System.Single,System.Int32,System.UInt32)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::Created(System.Boolean,System.UInt16)", "50abdb847e1337207a203d2df8824411037cc952ae834b460e0059b830c5f78a" },
            { "public instance System.Boolean BuildingManager::CreateBuilding(System.UInt16&,ColossalFramework.Math.Randomizer&,BuildingInfo,UnityEngine.Vector3,System.Single,System.Int32,System.UInt32)", "Finalizers", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks::End(System.UInt16,System.Boolean,System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityCreationHooks+State)", "ad34ec9cd86a91c8d6d0bb2d0c7bc36de78d69d7f91698ee404b71dc202ae4f5" },
            { "public instance System.Void BuildingManager::ReleaseBuilding(System.UInt16)", "Prefixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::BeginRelease(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State&)", "5b1e882f76f024909291667eb5e125cbc148efe7cb88eda5b0f5e8963508119d" },
            { "public instance System.Void BuildingManager::ReleaseBuilding(System.UInt16)", "Finalizers", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
            { "public instance System.Void ResidentialBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "Prefixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::BeginPhase(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State&)", "d1c60999685e910bf6f8715614d63b515857a2c347778a2ee4eba5105d5bb88c" },
            { "public instance System.Void ResidentialBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "Finalizers", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
            { "public instance System.Void Citizen::SetHome(System.UInt32,System.UInt16,System.UInt32)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeChanged(Citizen&)", "6d95ff3420ad2a719e7d9a65195cef10befa6a63d4cf849e556cbe0b23253907" },
            { "public instance System.Void CitizenUnit::SetWealthLevel(Citizen+Wealth)", "Postfixes", "SC4Evolution.ResidentialLotClass.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::HomeUnitChanged(CitizenUnit&)", "506b0c6a5fdbb874e60a377b0d9c223a4d743122969b63b529278e8ec3b21af6" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Prefixes", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::BeforeUpgrade(System.UInt16,SC4Evolution.Simulation.ResidentialLotIdentity&)", "e2f91b3281fb139d5fbf27a392a3c89544ee28fb77907ff4bdb2924d7dacaac0" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Prefixes", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::Begin(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State&)", "e310c461f3b980ec98a763c8255a9487e2a55927c6d593933292a558e1264913" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Postfixes", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialClassPatches::AfterUpgrade(SC4Evolution.Simulation.ResidentialLotIdentity)", "0b731a787ee765dd69f33f084b10c6ab9243a86ebd82868265ef244f3bda6238" },
            { "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "Finalizers", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
            { "public instance System.Void PrivateBuildingAI::BuildingUpgraded(System.UInt16,Building&)", "Prefixes", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::Begin(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State&)", "e310c461f3b980ec98a763c8255a9487e2a55927c6d593933292a558e1264913" },
            { "public instance System.Void PrivateBuildingAI::BuildingUpgraded(System.UInt16,Building&)", "Finalizers", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
            { "public instance System.Void PrivateBuildingAI::StartUpgrading(System.UInt16,Building&)", "Prefixes", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Void SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::Begin(System.UInt16,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State&)", "e310c461f3b980ec98a763c8255a9487e2a55927c6d593933292a558e1264913" },
            { "public instance System.Void PrivateBuildingAI::StartUpgrading(System.UInt16,Building&)", "Finalizers", "SC4Evolution.ResidentialLotIdentity.v1", "SC4Evolution, Version=0.5.1.0, Culture=neutral, PublicKeyToken=null", "private static System.Exception SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks::End(System.Exception,SC4Evolution.Mod.Economy.ResidentialCapacityMutationHooks+State)", "8525ae3e3b3fe89e5e0623b7e1a9c4960fb2bb3cb18a2f04493f07a774dff9e9" },
        };
        internal static int ExpectedMethodCount { get { return MethodPins.GetLength(0); } }
        internal static string[,] ApprovalMetadata { get { return (string[,])Approved.Clone(); } }
        internal static string ExpectedHash(string key)
        { for(int i=0;i<MethodPins.GetLength(0);i++) if(MethodPins[i,0]==key)return MethodPins[i,1];return null; }
        internal static int Match(ResidentialCapacityPatchObservation patch)
        {
            if(patch==null||!patch.DefaultOrdering)return -1;
            for(int i=0;i<Approved.GetLength(0);i++)
                if(patch.Target==Approved[i,0]&&patch.Kind==Approved[i,1]&&patch.Owner==Approved[i,2]&&patch.Hook==Approved[i,3]+" | "+Approved[i,4]&&patch.HookCanonicalSha256==Approved[i,5])return i;
            return -1;
        }
        internal static MethodBase[] RequireMethods(Assembly[] assemblies)
        { return RequireMethods(assemblies,ResidentialCapacityCanonicalIL.RawMaterial); }
        internal static MethodBase[] RequireMethods(Assembly[] assemblies,Func<MethodBase,string> material)
        { return ResidentialCapacityOwnHooks.RequirePinnedMethods(assemblies,material,MethodPins,Sources); }
        internal static bool IsSource(Assembly assembly)
        { for(int i=0;i<Sources.GetLength(0);i++)if(assembly.FullName==Sources[i,1])return true;return false; }
    }

    internal enum ResidentialCapacityCompatibilityKind { Unknown, Known }
    // Each positive configuration describes only its exact covered boundary.
    // RAW additionally requires its reviewed helper manifest and certificate.
    internal enum ResidentialCapacityEffectiveConfiguration { NotInspected, Unknown, OriginalOnly, OwnLotClassV1, OwnRawCapacityV1 }

    internal sealed class ResidentialCapacityMethodObservation
    {
        internal readonly string Signature, OriginalIlSha256, OriginalBodySha256, Reason;
        internal readonly bool Known;
        internal ResidentialCapacityMethodObservation(string signature, string il, string body, bool known, string reason)
        { Signature = signature; OriginalIlSha256 = il; OriginalBodySha256 = body; Known = known; Reason = reason; }
    }
    internal sealed class ResidentialCapacityPatchObservation
    {
        internal readonly string Target, Kind, Owner, Hook, HookIlSha256, HookCanonicalSha256;
        internal readonly int Priority;
        private readonly string[] _before, _after;
        internal bool DefaultOrdering { get { return Priority == 400 && _before.Length == 0 && _after.Length == 0; } }
        internal string[] Before { get { return (string[])_before.Clone(); } }
        internal string[] After { get { return (string[])_after.Clone(); } }
        internal ResidentialCapacityPatchObservation(string target, string kind, string owner, string hook, string il, string canonical = null, int priority = 400, string[] before = null, string[] after = null)
        { Target = target; Kind = kind; Owner = owner; Hook = hook; HookIlSha256 = il; HookCanonicalSha256 = canonical; Priority = priority;
            _before = before == null ? new string[0] : (string[])before.Clone(); _after = after == null ? new string[0] : (string[])after.Clone(); }
    }
    internal sealed class ResidentialCapacityCompatibilityResult
    {
        private readonly ResidentialCapacityMethodObservation[] _methods;
        private readonly ResidentialCapacityPatchObservation[] _patches;
        internal readonly ResidentialCapacityCompatibilityKind Kind;
        internal readonly string Scope, Reason;
        internal readonly bool OriginalMethodsKnown, EffectiveMetadataInspected;
        internal readonly ResidentialCapacityEffectiveConfiguration EffectiveConfiguration;
        internal ResidentialCapacityMethodObservation[] Methods { get { return (ResidentialCapacityMethodObservation[])_methods.Clone(); } }
        internal ResidentialCapacityPatchObservation[] Patches { get { return (ResidentialCapacityPatchObservation[])_patches.Clone(); } }
        internal ResidentialCapacityCompatibilityResult(bool known, string scope, string reason, bool original, bool effective,
            ResidentialCapacityMethodObservation[] methods, ResidentialCapacityPatchObservation[] patches, ResidentialCapacityEffectiveConfiguration configuration = ResidentialCapacityEffectiveConfiguration.NotInspected)
        { Kind = known ? ResidentialCapacityCompatibilityKind.Known : ResidentialCapacityCompatibilityKind.Unknown;
            Scope = scope; Reason = reason; OriginalMethodsKnown = original; EffectiveMetadataInspected = effective;
            EffectiveConfiguration = !effective ? ResidentialCapacityEffectiveConfiguration.NotInspected : !known ? ResidentialCapacityEffectiveConfiguration.Unknown : configuration == ResidentialCapacityEffectiveConfiguration.OwnLotClassV1 || configuration == ResidentialCapacityEffectiveConfiguration.OwnRawCapacityV1 ? configuration : ResidentialCapacityEffectiveConfiguration.OriginalOnly;
            _methods = (ResidentialCapacityMethodObservation[])methods.Clone(); _patches = (ResidentialCapacityPatchObservation[])patches.Clone(); }
    }

    // Captures references, not a mutable view of the registry or an authority
    // lease. Normal pinned Harmony UpdatePatchInfo allocates a fresh byte[] for
    // every published value. Direct in-place/reflection tampering is excluded.
    internal sealed class ResidentialCapacityRegistrySnapshot
    {
        private sealed class Entry
        {
            internal readonly MethodBase Key; internal readonly bool Present; internal readonly object Value;
            internal Entry(MethodBase key, bool present, object value) { Key = key; Present = present; Value = value; }
        }
        private readonly Entry[] _entries;
        private readonly MethodInfo[] _readerDefinitions;
        internal int Count { get { return _entries.Length; } }
        internal ResidentialCapacityRegistrySnapshot(IDictionary state, MethodBase[] methods, MethodInfo readerDefinition):this(state,methods,new[]{readerDefinition}) { }
        internal ResidentialCapacityRegistrySnapshot(IDictionary state, MethodBase[] methods, MethodInfo[] readerDefinitions)
        {
            if (state == null || methods == null || readerDefinitions == null || readerDefinitions.Length==0) throw new ArgumentNullException("state");
            _readerDefinitions=(MethodInfo[])readerDefinitions.Clone();foreach(MethodInfo reader in _readerDefinitions)if(reader==null)throw new ArgumentNullException("readerDefinitions"); List<Entry> entries = new List<Entry>(); HashSet<MethodBase> seen = new HashSet<MethodBase>();
            foreach (MethodBase method in methods) if (seen.Add(method))
            {
                bool present = state.Contains(method); object value = present ? state[method] : null;
                if (present && !(value is byte[])) throw new InvalidOperationException("Provider registry has an unsupported value.");
                entries.Add(new Entry(method, present, value));
            }
            _entries = entries.ToArray(); string reason;
            if (!Matches(state, out reason)) throw new InvalidOperationException(reason);
        }
        internal bool Matches(IDictionary state, out string reason)
        {
            foreach (Entry entry in _entries)
                if (state.Contains(entry.Key) != entry.Present || (entry.Present && !ReferenceEquals(state[entry.Key], entry.Value)))
                { reason = "A covered patch-registry entry changed: " + ResidentialCapacityCompatibility.Signature(entry.Key); return false; }
            foreach (object key in state.Keys)
            foreach(MethodInfo reader in _readerDefinitions)
                if (ResidentialCapacityHarmonyMetadata.IsDictionaryReaderFamily(key as MethodBase, reader))
                { reason = "The provider dictionary-reader generic family was patched."; return false; }
            reason = null; return true;
        }
    }

    // Nonblocking multi-fork synchronization. No provider monitor may escape
    // this scope or remain held across a native game call.
    internal sealed class ResidentialCapacityProviderLocks : IDisposable
    {
        private readonly List<object> _held = new List<object>();
        private ResidentialCapacityProviderLocks() { }
        internal static bool TryAcquire(object[] lockers, object state, out ResidentialCapacityProviderLocks scope, out string reason)
        {
            scope=new ResidentialCapacityProviderLocks();
            try
            {
                foreach(object item in lockers)
                {
                    if(item==null||ReferenceEquals(item,state))throw new InvalidOperationException("Invalid provider synchronization identity.");
                    foreach(object previous in scope._held)if(ReferenceEquals(previous,item))throw new InvalidOperationException("Distinct provider lockers unexpectedly alias.");
                    if(!System.Threading.Monitor.TryEnter(item)){reason="A provider synchronization monitor is busy.";scope.Dispose();scope=null;return false;}
                    scope._held.Add(item);
                }
                if(state==null||!System.Threading.Monitor.TryEnter(state)){reason="The common provider state monitor is busy.";scope.Dispose();scope=null;return false;}
                scope._held.Add(state);reason=null;return true;
            }
            catch { scope.Dispose();scope=null;throw; }
        }
        public void Dispose(){for(int i=_held.Count-1;i>=0;i--)System.Threading.Monitor.Exit(_held[i]);_held.Clear();}
    }

    // Proof of explicit initialization and shared REFERENCES, not authority to
    // control growth. Its construction is restricted to the reviewed factory.
    internal sealed class ResidentialCapacityProviderActivation
    {
        internal sealed class Fork
        {
            internal readonly Assembly Assembly;
            internal readonly FieldInfo LockerField, StateField, OriginalsField, VersionField;
            internal readonly object Locker;
            internal readonly MethodInfo Reader;
            internal readonly MethodBase[] Keys;
            internal Fork(Assembly assembly,FieldInfo locker,FieldInfo state,FieldInfo originals,FieldInfo version,object lockerValue,MethodInfo reader,MethodBase[] keys)
            {Assembly=assembly;LockerField=locker;StateField=state;OriginalsField=originals;VersionField=version;Locker=lockerValue;Reader=reader;Keys=(MethodBase[])keys.Clone();}
        }
        private readonly Fork[] _forks;
        internal readonly Type SharedType;
        internal readonly object State, Originals;
        internal readonly FieldInfo GlobalState, GlobalOriginals, GlobalVersion;
        internal ResidentialCapacityProviderActivation(Fork[] forks,Type shared,FieldInfo state,FieldInfo originals,FieldInfo version,object stateValue,object originalsValue)
        {_forks=(Fork[])forks.Clone();SharedType=shared;GlobalState=state;GlobalOriginals=originals;GlobalVersion=version;State=stateValue;Originals=originalsValue;}
        internal object[] Lockers{get{object[] value=new object[_forks.Length];for(int i=0;i<value.Length;i++)value[i]=_forks[i].Locker;return value;}}
        internal MethodBase[] Keys{get{List<MethodBase> value=new List<MethodBase>();foreach(Fork fork in _forks)value.AddRange(fork.Keys);return value.ToArray();}}
        internal MethodInfo[] Readers{get{MethodInfo[] value=new MethodInfo[_forks.Length];for(int i=0;i<value.Length;i++)value[i]=_forks[i].Reader;return value;}}
        internal int ForkCount{get{return _forks.Length;}}
        internal Assembly[] Assemblies{get{Assembly[] result=new Assembly[_forks.Length];for(int i=0;i<result.Length;i++)result[i]=_forks[i].Assembly;return result;}}
        internal bool Allows(Assembly assembly){foreach(Fork fork in _forks)if(ReferenceEquals(fork.Assembly,assembly))return true;return false;}
        internal bool Validate(Assembly[] current,out string reason)
        {
            foreach(Fork fork in _forks)
            {
                bool found=false;
                foreach(Assembly actual in current)if(actual.FullName==fork.Assembly.FullName)
                {if(!ReferenceEquals(actual,fork.Assembly)){reason="A provider family identity is duplicated/replaced.";return false;}found=true;}
                if(!found){reason="An activated provider family member is absent.";return false;}
            }
            foreach(Assembly assembly in current)
            {
                string simple=assembly.GetName().Name;
                if((simple=="0Harmony"||simple=="CitiesHarmony.Harmony")&&!Allows(assembly)){reason="An extra/replaced provider family assembly is loaded.";return false;}
                Type harmony=assembly.GetType("HarmonyLib.Harmony",false);
                if(harmony!=null&&!Allows(harmony.Assembly)){reason="An unapproved concrete Harmony2 provider is loaded.";return false;}
                Type shared=assembly.GetType("CHSharedState",false);
                if(shared!=null&&!ReferenceEquals(shared,SharedType)){reason="Another central provider state type appeared.";return false;}
            }
            if(!ReferenceEquals(GlobalState.GetValue(null),State)||!ReferenceEquals(GlobalOriginals.GetValue(null),Originals)||!object.Equals(GlobalVersion.GetValue(null),102))
            {reason="The generated shared-state references/version changed.";return false;}
            // All these cctors completed explicitly before this receipt existed.
            foreach(Fork fork in _forks)
                if(!ReferenceEquals(fork.LockerField.GetValue(null),fork.Locker)||!ReferenceEquals(fork.StateField.GetValue(null),State)||!ReferenceEquals(fork.OriginalsField.GetValue(null),Originals)||!object.Equals(fork.VersionField.GetValue(null),102))
                {reason="An activated provider private state/locker reference differs.";return false;}
            reason=null;return true;
        }
    }

    internal static class ResidentialCapacityProviderFamily
    {
        private const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
        private static readonly string[,] Identities=new string[,] {
            {"CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null","5de708ea-59c8-4f69-a6f5-3f97631a387f"},
            {"0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null","876cfcde-0b34-4438-abe9-feb250711b2d"},
            {"0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null","f45468f2-d262-451f-8c05-6393aa2ae646"},
            {"0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null","354c3a2f-b5d2-4907-ac3f-8fb9edb7115a"}
        };
        private static readonly string[,] Pins=new string[,] {
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_0", "other instance System.Boolean HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_0(System.Reflection.Assembly)", "20", "cf1c59e1f2cb5777320155564d1ba6884957e3c6360a436a1369d4da658075e3", "07d3cfac06b2f9a49cac61cd138f757cd866c442319fdffe0ef9c4210d13f0da", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_1", "other instance System.Collections.Generic.IEnumerable\u00601[[System.Reflection.Module, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]] HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_1(System.Reflection.Assembly)", "7", "6597dd9db35dce4c6c1db8b201b8f2b1b6db2b69eddb4a505751fe8edbf71f94", "11326bb9d66a7f7a3eedbd0f5744b8399174ae1ba89ca575bc46eb7ac0676f3f", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", "\u003C.cctor\u003Eb__7_0", "other instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::\u003C.cctor\u003Eb__7_0(System.Reflection.MethodBase,System.IntPtr,System.UInt64)", "30", "301894059949435d1ee260e33f78da51be9ebeadd990d28374353bb24310e121", "d9d32dedfec4a2f04ad0650f2cf01ffe6af267c982c6ff06f791c989ddd7fb92", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetPatchInfo", "other static HarmonyLib.PatchInfo HarmonyLib.HarmonySharedState::GetPatchInfo(System.Reflection.MethodBase)", "45", "025f816e51cfa28290cccf131041fb9c5ff5a7d19065c3548b93793bdfb9326c", "0f017a35efb028f7d53949937a6126d72cf0d35abacb334d808f7244f026b765", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Deserialize", "other static HarmonyLib.PatchInfo HarmonyLib.PatchInfoSerialization::Deserialize(System.Byte[])", "38", "7260ba4e9c2e6632211a035b4a38133f075cfb45e894a3d86ef8959cf80cfc4c", "0d25604d3b0adad3e5bdb436d2655976b85424e93dc661c619f45bfb1cdc01f0", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Serialize", "other static System.Byte[] HarmonyLib.PatchInfoSerialization::Serialize(HarmonyLib.PatchInfo)", "39", "c5a76a666720de778dc216faac3cb8f1f2343a880ddb9db1a5dfc5ec01cc6f72", "b29f7144b737865f0fcb3631d775ded801f8f4fa841b0766b4c12c720df34419", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "UpdatePatchInfo", "other static System.Void HarmonyLib.HarmonySharedState::UpdatePatchInfo(System.Reflection.MethodBase,System.Reflection.MethodInfo,HarmonyLib.PatchInfo)", "121", "b29c0516d18c7df4388189aeb3947bb6fdf281a2de28ad385eea051d3f1b200c", "2c09bd1271ac3f0523eb07264a9205953d391e3646f336f95a5e984e09dc8be7", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "\u003Cget_PatchMethod\u003Eb__10_2", "private instance System.Boolean HarmonyLib.Patch::\u003Cget_PatchMethod\u003Eb__10_2(System.Reflection.Module)", "32", "b0c927f633a1fd6277744eb6c169fd8fe76011126f97067560d3470d9b8849c6", "a41236a9613af467c13fe3da4f81e2543f4532213e34fdae4de84fbba1571661", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetOrCreateSharedStateType", "private static System.Type HarmonyLib.HarmonySharedState::GetOrCreateSharedStateType()", "253", "c9ebca7edd7f269a96719e1a3a9b2f603156d337afdf7feb2f9e2fe096886e18", "59a6755cf091f282ffd27d402f6b8b0f6dae8a483441accf6bc080a2f5fc9353", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.cctor()", "11", "a6decc11f20a016dccdfe74506a907aa03e595f0631e316275e8a2dea8f10f4c", "d72f7892f10b77e7bcdf5870181e50a29d06957e5d20f83d6da2c0059407100f", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState::.cctor()", "246", "94c6da3bb7f98a165fe35816e43a8a7cc9e0d3b36ed82cdee2ed27ebe35c85e5", "06d543bad079771726a921dec010c6bd278e9343b53e65d08faa07a5615a90b1", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "RefreshMethodStarts", "private static System.Void HarmonyLib.HarmonySharedState::RefreshMethodStarts()", "112", "597fdfd79f7240952509d7f585fe3f248f08a6b4168817b4934f896c01df2c3f", "3ec722500ee676d456fa6e0789ea6eb831e7ecc15e8cbdb632ac818d3ea25fb1", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.cctor()", "11", "3f4efe4cade4b6fd7d96d38c734cee85034d967ca7d445f2ef190c780ca1a131", "3eb70ef5ce8e46e2943a21effa26948ba6e0ec15af2f3985e07017d85da43c2a", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", ".cctor", "private static System.Void HarmonyLib.PatchInfoSerialization::.cctor()", "22", "13aee74dab38f0c8f44f08bb9ac7270c0634ad5bd7741fbde4b9f61e54e9331e", "f48199e9974294a95bf9ba95b6efad5ab55f83d285a997d60e820d4adf7186a5", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", ".cctor", "private static System.Void HarmonyLib.PatchProcessor::.cctor()", "11", "c6560be813e7e4925e58305ea2c0e5d5fdb0d559f7ef4f9404cb3864bf188413", "6bec086d20f20b93b355c0bff7bc505ca9baa35c301e7905c2aef3bdc0892147", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(HarmonyLib.HarmonyPatchType,System.String)", "128", "b0862c443f04da1d4ef056674adf5467e7fd3bc2f5baffd64f43a0d510a95ef9", "bcabd967d6382538248fcc104e9eb0fcb4b55558cbc021cd7e412d0b04172b24", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(System.Reflection.MethodInfo)", "79", "f02a1010a2d264f7693c2eb5ef70eb12d5a155219b58bb3a81589c902fcdd578", "ccd5425fc05899e6b444a7dca91d44ad338d0209f4a1377a285c49f73584660b", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "get_PatchMethod", "public instance System.Reflection.MethodInfo HarmonyLib.Patch::get_PatchMethod()", "138", "e217b44aad0154e073da46658f259b9b7da798d9c000f308050e2116d6554fbc", "eaf1f3feb184af5f60ad3e71d2944ca2b448b3a8da488e2f2bf448846ad76626", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Patch", "public instance System.Reflection.MethodInfo HarmonyLib.PatchProcessor::Patch()", "289", "e2ab029607303cccacd2b3bad9f431029cc8981eb9ec153967e1e0b505c37404", "f74a0903a3fd8704200d62a442b80b1271dde1ab1b6dabc66f6be6146db67473", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", "BindToType", "public instance System.Type HarmonyLib.PatchInfoSerialization\u002BBinder::BindToType(System.String,System.String)", "98", "a0d3d5e8efdd9c04a47b38d7a4dbede890c07f953a58580e5b13c2a6298f741d", "20ada12985ff5ee058368e01facc8e3ec413bf8a2bb5a2050c8782fd1480e3be", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", ".ctor", "public instance System.Void HarmonyLib.PatchInfoSerialization\u002BBinder::.ctor()", "7", "79482b07da3cb366dabdeb668e0b64e6ae9ab931fc31cabc39b7e0973f292184", "a894cad35429bc88509bc23d83b7752d46685f3499ad6fe3b678f7e3c509e833", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patches", ".ctor", "public instance System.Void HarmonyLib.Patches::.ctor(HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[])", "121", "2e0e4f15bb1b1d793af5fc69e6a8de2a5a479773448c4ff6523a3b4fffe6d75b", "3750a79ae9cbd6948ad5bf5716ece9ac59359cd32cf73d27e089d1cb1d4c6935", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.GeneralExtensions", "GetValueSafe", "public static  HarmonyLib.GeneralExtensions::GetValueSafe(,)", "23", "7addb97526a38270460c03247cefe9a307305887ddd890a05ca112df288495d7", "1c83beb7b8a669080cd3177797ac7df903f7e93ecc2d2bd66990a47f519be3d5", "5451ce88f0f3315a4100d16a7f90df4ab4cc840cf74b3dec3d7f5ba5368431f9" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Harmony", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)", "7", "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892", "289d55b90417ea058d8dab2d16c13b9216ed3acd130ee303208ab432f18b21a0", "" },
            { "CitiesHarmony.Harmony, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.PatchProcessor::GetPatchInfo(System.Reflection.MethodBase)", "63", "a7f9b75ee694a08b0715e56e55e203fba27821ff713cf5da71f45f6671f97812", "f2b67caa0b9521e323e46f1bebaa5585de8836f3235dcddb2dfe623104780ed4", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_0", "other instance System.Boolean HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_0(System.Reflection.Assembly)", "20", "cf1c59e1f2cb5777320155564d1ba6884957e3c6360a436a1369d4da658075e3", "07d3cfac06b2f9a49cac61cd138f757cd866c442319fdffe0ef9c4210d13f0da", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_1", "other instance System.Collections.Generic.IEnumerable\u00601[[System.Reflection.Module, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]] HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_1(System.Reflection.Assembly)", "7", "6597dd9db35dce4c6c1db8b201b8f2b1b6db2b69eddb4a505751fe8edbf71f94", "11326bb9d66a7f7a3eedbd0f5744b8399174ae1ba89ca575bc46eb7ac0676f3f", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", "\u003C.cctor\u003Eb__7_0", "other instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::\u003C.cctor\u003Eb__7_0(System.Reflection.MethodBase,System.IntPtr,System.UInt64)", "30", "301894059949435d1ee260e33f78da51be9ebeadd990d28374353bb24310e121", "d9d32dedfec4a2f04ad0650f2cf01ffe6af267c982c6ff06f791c989ddd7fb92", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetPatchInfo", "other static HarmonyLib.PatchInfo HarmonyLib.HarmonySharedState::GetPatchInfo(System.Reflection.MethodBase)", "45", "025f816e51cfa28290cccf131041fb9c5ff5a7d19065c3548b93793bdfb9326c", "0f017a35efb028f7d53949937a6126d72cf0d35abacb334d808f7244f026b765", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Deserialize", "other static HarmonyLib.PatchInfo HarmonyLib.PatchInfoSerialization::Deserialize(System.Byte[])", "38", "10c8360bd1c46feaab995e15e98c097775d5bb985733983c6f747d438c2ad7a1", "b841ea31bcaa933f1a39219ccdd6b3302782330c7651bd5052d04b0eb17a83ad", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Serialize", "other static System.Byte[] HarmonyLib.PatchInfoSerialization::Serialize(HarmonyLib.PatchInfo)", "39", "06daedd6e67d0c79d9e702443f2197e0ba9bf168dbb6753b8aa48fee81496b5e", "b42b8bd14bfd4c9319b10927b576172315926e2f48bd4f92ac86e7f955939f87", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "UpdatePatchInfo", "other static System.Void HarmonyLib.HarmonySharedState::UpdatePatchInfo(System.Reflection.MethodBase,System.Reflection.MethodInfo,HarmonyLib.PatchInfo)", "121", "b29c0516d18c7df4388189aeb3947bb6fdf281a2de28ad385eea051d3f1b200c", "2c09bd1271ac3f0523eb07264a9205953d391e3646f336f95a5e984e09dc8be7", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "\u003Cget_PatchMethod\u003Eb__10_2", "private instance System.Boolean HarmonyLib.Patch::\u003Cget_PatchMethod\u003Eb__10_2(System.Reflection.Module)", "32", "9e5fab1cdb47431775482980c34b732e87b09da377b83766e40cd18a47a8bdcb", "c9e21a7bebed4fc9598cd8e820c6271a4ee892ca876effd5939ae2bf3206e600", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetOrCreateSharedStateType", "private static System.Type HarmonyLib.HarmonySharedState::GetOrCreateSharedStateType()", "253", "ba368b47223258b8e372f1ed99becbd8a45b2de119862103fd4352b419698655", "0395205cb925379d3d02c6dffb6051377d66ac16f1d389a0d1db62ed86b7d2fc", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.cctor()", "11", "a6decc11f20a016dccdfe74506a907aa03e595f0631e316275e8a2dea8f10f4c", "d72f7892f10b77e7bcdf5870181e50a29d06957e5d20f83d6da2c0059407100f", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState::.cctor()", "246", "94c6da3bb7f98a165fe35816e43a8a7cc9e0d3b36ed82cdee2ed27ebe35c85e5", "06d543bad079771726a921dec010c6bd278e9343b53e65d08faa07a5615a90b1", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "RefreshMethodStarts", "private static System.Void HarmonyLib.HarmonySharedState::RefreshMethodStarts()", "112", "597fdfd79f7240952509d7f585fe3f248f08a6b4168817b4934f896c01df2c3f", "3ec722500ee676d456fa6e0789ea6eb831e7ecc15e8cbdb632ac818d3ea25fb1", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.cctor()", "11", "adbf35583e767679b077f57ef89573590da71c5611f358a1ec59de6db90df687", "29595b97f397e58766c35bf2534a3ac729074ef1a610c21568b9b76503f01039", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", ".cctor", "private static System.Void HarmonyLib.PatchInfoSerialization::.cctor()", "22", "0eea8dc187e6272b3f1b2e1b10ef20469692dfa0a80283bb6420aa1abc98000f", "ac349714092ca2fe31abaf9422711296663ee288d75168ff94cfe1b1bec7296f", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", ".cctor", "private static System.Void HarmonyLib.PatchProcessor::.cctor()", "11", "b053497d776041e3097f1832615e72736da752adc5a601832d8321e7391def4f", "a1b4e4ce55c4c3e7c4addd9fd2291105fc544db77756833d20df34774c869d37", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(HarmonyLib.HarmonyPatchType,System.String)", "128", "3199244d576f40aafd12a5d6a45dab268dfda42f48513dd6ce2bb3544fda3858", "d817f3bff03979544ba000fa9799275415f519e00b5490ed9fd9fe1676df3175", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(System.Reflection.MethodInfo)", "79", "8264415be40b63e338427ff4736988b59eaa241944fe983d6106180f0fd659ee", "c96bac1a624304a334fd462ad1dbefd79f27c8ec377195575743173276dba6f4", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "get_PatchMethod", "public instance System.Reflection.MethodInfo HarmonyLib.Patch::get_PatchMethod()", "138", "732d06da79c29ead22f9a416c1a584347b62db0905c58ffe666d246e25ecad6c", "ec7995a0db9e9d73e5c63775009ad74cfe0e1b5b621738b5a6f347500d9d3161", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Patch", "public instance System.Reflection.MethodInfo HarmonyLib.PatchProcessor::Patch()", "289", "33cb7ab694681dbcb68513c876105f68b2b9e7dda38b80ee185b3691b11286c8", "3d77f892c3b8f7387425cac5a0871d6da93ba68d1d27786316a3d391be352ce3", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", "BindToType", "public instance System.Type HarmonyLib.PatchInfoSerialization\u002BBinder::BindToType(System.String,System.String)", "98", "c43698ef81e1d5efe139f5846582c162767d7ef260e1cea2ccaac784ecd087f0", "53575ebae01ac848c025341f621006ce843a5e0727704e06a2972b259277f6ea", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", ".ctor", "public instance System.Void HarmonyLib.PatchInfoSerialization\u002BBinder::.ctor()", "7", "79482b07da3cb366dabdeb668e0b64e6ae9ab931fc31cabc39b7e0973f292184", "a894cad35429bc88509bc23d83b7752d46685f3499ad6fe3b678f7e3c509e833", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patches", ".ctor", "public instance System.Void HarmonyLib.Patches::.ctor(HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[])", "121", "14e9413ae8ccab99f8e099ebb71e2744ad089d0989c1ab2de3c9cebc0f8ae263", "d445595c1f3645c0b4a30a045bb4cd9dd936cffb51f11e4f7d60e554ba556d31", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.GeneralExtensions", "GetValueSafe", "public static  HarmonyLib.GeneralExtensions::GetValueSafe(,)", "23", "7addb97526a38270460c03247cefe9a307305887ddd890a05ca112df288495d7", "1c83beb7b8a669080cd3177797ac7df903f7e93ecc2d2bd66990a47f519be3d5", "b1e5d563355480815e03322c2cb39233e53195ce0c2fe9ffd147dded20163a3c" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Harmony", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)", "7", "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892", "289d55b90417ea058d8dab2d16c13b9216ed3acd130ee303208ab432f18b21a0", "" },
            { "0Harmony, Version=2.0.0.8, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.PatchProcessor::GetPatchInfo(System.Reflection.MethodBase)", "63", "8a0b656fbcfe0fdb5ea7088595bbb6f0930d44eda01629d527eae2e0d1a75e92", "484abc8ba3ed4d517fb593c022b48aac12ce7fe6f9e5f99df46d03c82c3cecf1", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_0", "other instance System.Boolean HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_0(System.Reflection.Assembly)", "20", "cf1c59e1f2cb5777320155564d1ba6884957e3c6360a436a1369d4da658075e3", "07d3cfac06b2f9a49cac61cd138f757cd866c442319fdffe0ef9c4210d13f0da", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_1", "other instance System.Collections.Generic.IEnumerable\u00601[[System.Reflection.Module, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]] HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_1(System.Reflection.Assembly)", "7", "6597dd9db35dce4c6c1db8b201b8f2b1b6db2b69eddb4a505751fe8edbf71f94", "11326bb9d66a7f7a3eedbd0f5744b8399174ae1ba89ca575bc46eb7ac0676f3f", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", "\u003C.cctor\u003Eb__7_0", "other instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::\u003C.cctor\u003Eb__7_0(System.Reflection.MethodBase,System.IntPtr,System.UInt64)", "30", "301894059949435d1ee260e33f78da51be9ebeadd990d28374353bb24310e121", "d9d32dedfec4a2f04ad0650f2cf01ffe6af267c982c6ff06f791c989ddd7fb92", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetPatchInfo", "other static HarmonyLib.PatchInfo HarmonyLib.HarmonySharedState::GetPatchInfo(System.Reflection.MethodBase)", "45", "025f816e51cfa28290cccf131041fb9c5ff5a7d19065c3548b93793bdfb9326c", "0f017a35efb028f7d53949937a6126d72cf0d35abacb334d808f7244f026b765", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Deserialize", "other static HarmonyLib.PatchInfo HarmonyLib.PatchInfoSerialization::Deserialize(System.Byte[])", "38", "10c8360bd1c46feaab995e15e98c097775d5bb985733983c6f747d438c2ad7a1", "b841ea31bcaa933f1a39219ccdd6b3302782330c7651bd5052d04b0eb17a83ad", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Serialize", "other static System.Byte[] HarmonyLib.PatchInfoSerialization::Serialize(HarmonyLib.PatchInfo)", "39", "06daedd6e67d0c79d9e702443f2197e0ba9bf168dbb6753b8aa48fee81496b5e", "b42b8bd14bfd4c9319b10927b576172315926e2f48bd4f92ac86e7f955939f87", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "UpdatePatchInfo", "other static System.Void HarmonyLib.HarmonySharedState::UpdatePatchInfo(System.Reflection.MethodBase,System.Reflection.MethodInfo,HarmonyLib.PatchInfo)", "121", "b29c0516d18c7df4388189aeb3947bb6fdf281a2de28ad385eea051d3f1b200c", "2c09bd1271ac3f0523eb07264a9205953d391e3646f336f95a5e984e09dc8be7", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "\u003Cget_PatchMethod\u003Eb__10_2", "private instance System.Boolean HarmonyLib.Patch::\u003Cget_PatchMethod\u003Eb__10_2(System.Reflection.Module)", "32", "9e5fab1cdb47431775482980c34b732e87b09da377b83766e40cd18a47a8bdcb", "c9e21a7bebed4fc9598cd8e820c6271a4ee892ca876effd5939ae2bf3206e600", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetOrCreateSharedStateType", "private static System.Type HarmonyLib.HarmonySharedState::GetOrCreateSharedStateType()", "253", "ba368b47223258b8e372f1ed99becbd8a45b2de119862103fd4352b419698655", "0395205cb925379d3d02c6dffb6051377d66ac16f1d389a0d1db62ed86b7d2fc", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.cctor()", "11", "a6decc11f20a016dccdfe74506a907aa03e595f0631e316275e8a2dea8f10f4c", "d72f7892f10b77e7bcdf5870181e50a29d06957e5d20f83d6da2c0059407100f", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState::.cctor()", "246", "94c6da3bb7f98a165fe35816e43a8a7cc9e0d3b36ed82cdee2ed27ebe35c85e5", "06d543bad079771726a921dec010c6bd278e9343b53e65d08faa07a5615a90b1", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "RefreshMethodStarts", "private static System.Void HarmonyLib.HarmonySharedState::RefreshMethodStarts()", "112", "597fdfd79f7240952509d7f585fe3f248f08a6b4168817b4934f896c01df2c3f", "3ec722500ee676d456fa6e0789ea6eb831e7ecc15e8cbdb632ac818d3ea25fb1", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.cctor()", "11", "adbf35583e767679b077f57ef89573590da71c5611f358a1ec59de6db90df687", "29595b97f397e58766c35bf2534a3ac729074ef1a610c21568b9b76503f01039", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", ".cctor", "private static System.Void HarmonyLib.PatchInfoSerialization::.cctor()", "22", "0eea8dc187e6272b3f1b2e1b10ef20469692dfa0a80283bb6420aa1abc98000f", "ac349714092ca2fe31abaf9422711296663ee288d75168ff94cfe1b1bec7296f", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", ".cctor", "private static System.Void HarmonyLib.PatchProcessor::.cctor()", "11", "b053497d776041e3097f1832615e72736da752adc5a601832d8321e7391def4f", "a1b4e4ce55c4c3e7c4addd9fd2291105fc544db77756833d20df34774c869d37", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(HarmonyLib.HarmonyPatchType,System.String)", "128", "3199244d576f40aafd12a5d6a45dab268dfda42f48513dd6ce2bb3544fda3858", "d817f3bff03979544ba000fa9799275415f519e00b5490ed9fd9fe1676df3175", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(System.Reflection.MethodInfo)", "79", "8264415be40b63e338427ff4736988b59eaa241944fe983d6106180f0fd659ee", "c96bac1a624304a334fd462ad1dbefd79f27c8ec377195575743173276dba6f4", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "get_PatchMethod", "public instance System.Reflection.MethodInfo HarmonyLib.Patch::get_PatchMethod()", "138", "732d06da79c29ead22f9a416c1a584347b62db0905c58ffe666d246e25ecad6c", "ec7995a0db9e9d73e5c63775009ad74cfe0e1b5b621738b5a6f347500d9d3161", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Patch", "public instance System.Reflection.MethodInfo HarmonyLib.PatchProcessor::Patch()", "289", "33cb7ab694681dbcb68513c876105f68b2b9e7dda38b80ee185b3691b11286c8", "3d77f892c3b8f7387425cac5a0871d6da93ba68d1d27786316a3d391be352ce3", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", "BindToType", "public instance System.Type HarmonyLib.PatchInfoSerialization\u002BBinder::BindToType(System.String,System.String)", "98", "c43698ef81e1d5efe139f5846582c162767d7ef260e1cea2ccaac784ecd087f0", "53575ebae01ac848c025341f621006ce843a5e0727704e06a2972b259277f6ea", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", ".ctor", "public instance System.Void HarmonyLib.PatchInfoSerialization\u002BBinder::.ctor()", "7", "79482b07da3cb366dabdeb668e0b64e6ae9ab931fc31cabc39b7e0973f292184", "a894cad35429bc88509bc23d83b7752d46685f3499ad6fe3b678f7e3c509e833", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patches", ".ctor", "public instance System.Void HarmonyLib.Patches::.ctor(HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[])", "121", "14e9413ae8ccab99f8e099ebb71e2744ad089d0989c1ab2de3c9cebc0f8ae263", "d445595c1f3645c0b4a30a045bb4cd9dd936cffb51f11e4f7d60e554ba556d31", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.GeneralExtensions", "GetValueSafe", "public static  HarmonyLib.GeneralExtensions::GetValueSafe(,)", "23", "7addb97526a38270460c03247cefe9a307305887ddd890a05ca112df288495d7", "1c83beb7b8a669080cd3177797ac7df903f7e93ecc2d2bd66990a47f519be3d5", "4ff7e501891eb03b64cf2707fe7189e23726e9948937588cd62c66b1f1ebc97a" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Harmony", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)", "7", "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892", "289d55b90417ea058d8dab2d16c13b9216ed3acd130ee303208ab432f18b21a0", "" },
            { "0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.PatchProcessor::GetPatchInfo(System.Reflection.MethodBase)", "63", "8a0b656fbcfe0fdb5ea7088595bbb6f0930d44eda01629d527eae2e0d1a75e92", "484abc8ba3ed4d517fb593c022b48aac12ce7fe6f9e5f99df46d03c82c3cecf1", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_0", "other instance System.Boolean HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_0(System.Reflection.Assembly)", "20", "cf1c59e1f2cb5777320155564d1ba6884957e3c6360a436a1369d4da658075e3", "07d3cfac06b2f9a49cac61cd138f757cd866c442319fdffe0ef9c4210d13f0da", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", "\u003Cget_PatchMethod\u003Eb__10_1", "other instance System.Collections.Generic.IEnumerable\u00601[[System.Reflection.Module, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]] HarmonyLib.Patch\u002B\u003C\u003Ec::\u003Cget_PatchMethod\u003Eb__10_1(System.Reflection.Assembly)", "7", "6597dd9db35dce4c6c1db8b201b8f2b1b6db2b69eddb4a505751fe8edbf71f94", "11326bb9d66a7f7a3eedbd0f5744b8399174ae1ba89ca575bc46eb7ac0676f3f", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", "\u003C.cctor\u003Eb__7_0", "other instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::\u003C.cctor\u003Eb__7_0(System.Reflection.MethodBase,System.IntPtr,System.UInt64)", "30", "301894059949435d1ee260e33f78da51be9ebeadd990d28374353bb24310e121", "d9d32dedfec4a2f04ad0650f2cf01ffe6af267c982c6ff06f791c989ddd7fb92", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetPatchInfo", "other static HarmonyLib.PatchInfo HarmonyLib.HarmonySharedState::GetPatchInfo(System.Reflection.MethodBase)", "45", "025f816e51cfa28290cccf131041fb9c5ff5a7d19065c3548b93793bdfb9326c", "0f017a35efb028f7d53949937a6126d72cf0d35abacb334d808f7244f026b765", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Deserialize", "other static HarmonyLib.PatchInfo HarmonyLib.PatchInfoSerialization::Deserialize(System.Byte[])", "38", "10c8360bd1c46feaab995e15e98c097775d5bb985733983c6f747d438c2ad7a1", "b841ea31bcaa933f1a39219ccdd6b3302782330c7651bd5052d04b0eb17a83ad", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", "Serialize", "other static System.Byte[] HarmonyLib.PatchInfoSerialization::Serialize(HarmonyLib.PatchInfo)", "39", "06daedd6e67d0c79d9e702443f2197e0ba9bf168dbb6753b8aa48fee81496b5e", "b42b8bd14bfd4c9319b10927b576172315926e2f48bd4f92ac86e7f955939f87", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "UpdatePatchInfo", "other static System.Void HarmonyLib.HarmonySharedState::UpdatePatchInfo(System.Reflection.MethodBase,System.Reflection.MethodInfo,HarmonyLib.PatchInfo)", "121", "b29c0516d18c7df4388189aeb3947bb6fdf281a2de28ad385eea051d3f1b200c", "2c09bd1271ac3f0523eb07264a9205953d391e3646f336f95a5e984e09dc8be7", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "\u003Cget_PatchMethod\u003Eb__10_2", "private instance System.Boolean HarmonyLib.Patch::\u003Cget_PatchMethod\u003Eb__10_2(System.Reflection.Module)", "32", "9e5fab1cdb47431775482980c34b732e87b09da377b83766e40cd18a47a8bdcb", "c9e21a7bebed4fc9598cd8e820c6271a4ee892ca876effd5939ae2bf3206e600", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "GetOrCreateSharedStateType", "private static System.Type HarmonyLib.HarmonySharedState::GetOrCreateSharedStateType()", "253", "ba368b47223258b8e372f1ed99becbd8a45b2de119862103fd4352b419698655", "0395205cb925379d3d02c6dffb6051377d66ac16f1d389a0d1db62ed86b7d2fc", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.cctor()", "11", "a6decc11f20a016dccdfe74506a907aa03e595f0631e316275e8a2dea8f10f4c", "d72f7892f10b77e7bcdf5870181e50a29d06957e5d20f83d6da2c0059407100f", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", ".cctor", "private static System.Void HarmonyLib.HarmonySharedState::.cctor()", "246", "94c6da3bb7f98a165fe35816e43a8a7cc9e0d3b36ed82cdee2ed27ebe35c85e5", "06d543bad079771726a921dec010c6bd278e9343b53e65d08faa07a5615a90b1", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState", "RefreshMethodStarts", "private static System.Void HarmonyLib.HarmonySharedState::RefreshMethodStarts()", "112", "597fdfd79f7240952509d7f585fe3f248f08a6b4168817b4934f896c01df2c3f", "3ec722500ee676d456fa6e0789ea6eb831e7ecc15e8cbdb632ac818d3ea25fb1", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".cctor", "private static System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.cctor()", "11", "adbf35583e767679b077f57ef89573590da71c5611f358a1ec59de6db90df687", "29595b97f397e58766c35bf2534a3ac729074ef1a610c21568b9b76503f01039", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization", ".cctor", "private static System.Void HarmonyLib.PatchInfoSerialization::.cctor()", "22", "0eea8dc187e6272b3f1b2e1b10ef20469692dfa0a80283bb6420aa1abc98000f", "ac349714092ca2fe31abaf9422711296663ee288d75168ff94cfe1b1bec7296f", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", ".cctor", "private static System.Void HarmonyLib.PatchProcessor::.cctor()", "11", "b053497d776041e3097f1832615e72736da752adc5a601832d8321e7391def4f", "a1b4e4ce55c4c3e7c4addd9fd2291105fc544db77756833d20df34774c869d37", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(HarmonyLib.HarmonyPatchType,System.String)", "128", "3199244d576f40aafd12a5d6a45dab268dfda42f48513dd6ce2bb3544fda3858", "d817f3bff03979544ba000fa9799275415f519e00b5490ed9fd9fe1676df3175", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(System.Reflection.MethodInfo)", "79", "8264415be40b63e338427ff4736988b59eaa241944fe983d6106180f0fd659ee", "c96bac1a624304a334fd462ad1dbefd79f27c8ec377195575743173276dba6f4", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch", "get_PatchMethod", "public instance System.Reflection.MethodInfo HarmonyLib.Patch::get_PatchMethod()", "138", "732d06da79c29ead22f9a416c1a584347b62db0905c58ffe666d246e25ecad6c", "ec7995a0db9e9d73e5c63775009ad74cfe0e1b5b621738b5a6f347500d9d3161", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "Patch", "public instance System.Reflection.MethodInfo HarmonyLib.PatchProcessor::Patch()", "289", "33cb7ab694681dbcb68513c876105f68b2b9e7dda38b80ee185b3691b11286c8", "3d77f892c3b8f7387425cac5a0871d6da93ba68d1d27786316a3d391be352ce3", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", "BindToType", "public instance System.Type HarmonyLib.PatchInfoSerialization\u002BBinder::BindToType(System.String,System.String)", "98", "c43698ef81e1d5efe139f5846582c162767d7ef260e1cea2ccaac784ecd087f0", "53575ebae01ac848c025341f621006ce843a5e0727704e06a2972b259277f6ea", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.HarmonySharedState\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patch\u002B\u003C\u003Ec", ".ctor", "public instance System.Void HarmonyLib.Patch\u002B\u003C\u003Ec::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchInfoSerialization\u002BBinder", ".ctor", "public instance System.Void HarmonyLib.PatchInfoSerialization\u002BBinder::.ctor()", "7", "79482b07da3cb366dabdeb668e0b64e6ae9ab931fc31cabc39b7e0973f292184", "a894cad35429bc88509bc23d83b7752d46685f3499ad6fe3b678f7e3c509e833", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Patches", ".ctor", "public instance System.Void HarmonyLib.Patches::.ctor(HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[])", "121", "14e9413ae8ccab99f8e099ebb71e2744ad089d0989c1ab2de3c9cebc0f8ae263", "d445595c1f3645c0b4a30a045bb4cd9dd936cffb51f11e4f7d60e554ba556d31", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.GeneralExtensions", "GetValueSafe", "public static  HarmonyLib.GeneralExtensions::GetValueSafe(,)", "23", "7addb97526a38270460c03247cefe9a307305887ddd890a05ca112df288495d7", "1c83beb7b8a669080cd3177797ac7df903f7e93ecc2d2bd66990a47f519be3d5", "d8e512b079c3ddcf186847f22f625577cffc2404fd8eed4f9e73bde59e830c01" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.Harmony", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)", "7", "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892", "289d55b90417ea058d8dab2d16c13b9216ed3acd130ee303208ab432f18b21a0", "" },
            { "0Harmony, Version=2.0.1.0, Culture=neutral, PublicKeyToken=null", "HarmonyLib.PatchProcessor", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.PatchProcessor::GetPatchInfo(System.Reflection.MethodBase)", "63", "8a0b656fbcfe0fdb5ea7088595bbb6f0930d44eda01629d527eae2e0d1a75e92", "484abc8ba3ed4d517fb593c022b48aac12ce7fe6f9e5f99df46d03c82c3cecf1", "" },
        };
        internal static Assembly[] Select(Assembly[] assemblies)
        {
            Assembly[] selected=new Assembly[4];
            foreach(Assembly assembly in assemblies)
            {
                Type harmony=assembly.GetType("HarmonyLib.Harmony",false);
                string simple=assembly.GetName().Name;
                if((simple=="0Harmony"||simple=="CitiesHarmony.Harmony")&&(harmony==null||!ReferenceEquals(harmony.Assembly,assembly)))throw new InvalidOperationException("Named provider family assembly does not declare its exact expected types.");
                if(harmony==null)continue;
                Assembly concrete=harmony.Assembly;int index=-1;
                for(int i=0;i<4;i++)if(concrete.FullName==Identities[i,0]&&concrete.ManifestModule.ModuleVersionId==new Guid(Identities[i,1]))index=i;
                if(index<0)throw new InvalidOperationException("An unapproved concrete Harmony2 provider is loaded: "+concrete.FullName);
                if(selected[index]!=null&&!ReferenceEquals(selected[index],concrete))throw new InvalidOperationException("Duplicate concrete provider family identity.");
                selected[index]=concrete;
            }
            if(selected[0]==null)throw new InvalidOperationException("The pinned central provider is absent.");
            List<Assembly> result=new List<Assembly>();
            for(int i=0;i<4;i++)if(selected[i]!=null)result.Add(selected[i]);
            return result.ToArray();
        }
        internal static MethodBase[] RequireMethods(Assembly assembly,Func<MethodBase,string> canonical)
        {
            int identity=-1;for(int i=0;i<4;i++)if(assembly.FullName==Identities[i,0]&&assembly.ManifestModule.ModuleVersionId==new Guid(Identities[i,1]))identity=i;
            if(identity<0)throw new InvalidOperationException("Unpinned provider family source.");
            List<MethodBase> keys=new List<MethodBase>();
            for(int i=0;i<Pins.GetLength(0);i++)if(Pins[i,0]==assembly.FullName)
            {
                Type type=assembly.GetType(Pins[i,1],true);MethodBase method=null;
                if(Pins[i,2]==".cctor")method=type.TypeInitializer;
                else
                {
                    List<MethodBase> candidates=new List<MethodBase>();
                    if(Pins[i,2]==".ctor")candidates.AddRange(type.GetConstructors(Static|BindingFlags.Instance));else candidates.AddRange(type.GetMethods(Static|BindingFlags.Instance));
                    foreach(MethodBase candidate in candidates)
                        if(ResidentialCapacityCompatibility.Signature(candidate)==Pins[i,3]||(Pins[i,2]=="GetValueSafe"&&candidate.Name=="GetValueSafe"))
                        {if(method!=null)throw new AmbiguousMatchException(Pins[i,3]);method=candidate;}
                }
                if(method==null)throw new MissingMethodException(Pins[i,3]);
                if(method.Name=="GetValueSafe")
                {
                    byte[] il=method.GetMethodBody().GetILAsByteArray();
                    if(il.Length!=int.Parse(Pins[i,4],CultureInfo.InvariantCulture)||ResidentialCapacityCompatibility.Hash(il)!=Pins[i,5]||ResidentialCapacityCompatibility.Hash(Encoding.UTF8.GetBytes(canonical(method)))!=Pins[i,7])throw new InvalidOperationException("Provider family generic reader body differs.");
                }
                else
                {
                    ResidentialCapacityCompatibility.MethodPin pin=new ResidentialCapacityCompatibility.MethodPin(assembly.GetName().Name,Pins[i,1],Pins[i,2],Pins[i,3],Pins[i,5],Pins[i,6],int.Parse(Pins[i,4],CultureInfo.InvariantCulture));
                    if(!ResidentialCapacityCompatibility.ObserveMethod(method,pin).Known)throw new InvalidOperationException("Provider family method body differs: "+Pins[i,3]);
                }
                keys.Add(method);
            }
            if(keys.Count!=27)throw new InvalidOperationException("Provider family boundary count differs.");
            MethodInfo reader=assembly.GetType("HarmonyLib.GeneralExtensions",true).GetMethod("GetValueSafe",Static);
            if(reader==null||!reader.IsGenericMethodDefinition||reader.GetGenericArguments().Length!=2)throw new InvalidOperationException("Provider family reader definition differs.");
            Type[] generic=reader.GetGenericArguments();ParameterInfo[] parameters=reader.GetParameters();
            if(parameters.Length!=2||reader.ReturnType!=generic[1]||parameters[1].ParameterType!=generic[0]||!parameters[0].ParameterType.IsGenericType||parameters[0].ParameterType.GetGenericTypeDefinition().FullName!="System.Collections.Generic.Dictionary`2"||parameters[0].ParameterType.GetGenericArguments()[0]!=generic[0]||parameters[0].ParameterType.GetGenericArguments()[1]!=generic[1])throw new InvalidOperationException("Provider family reader signature differs.");
            foreach(Type argument in generic)if(argument.GenericParameterAttributes!=GenericParameterAttributes.None||argument.GetGenericParameterConstraints().Length!=0)throw new InvalidOperationException("Provider family reader constraints differ.");
            Type key=assembly.GetType("HarmonyLib.Harmony",true).GetMethod("GetPatchInfo",Static).GetParameters()[0].ParameterType;
            Type bytes=assembly.GetType("HarmonyLib.PatchInfoSerialization",true).GetMethod("Deserialize",Static).GetParameters()[0].ParameterType;
            keys.Add(reader.MakeGenericMethod(key,bytes));return keys.ToArray();
        }
        internal static void RequireUnpatched(IDictionary state,MethodBase[] keys,MethodInfo[] readers)
        {
            foreach(MethodBase key in keys)if(state.Contains(key))throw new InvalidOperationException("Provider family reader/writer/initializer has a patch-state entry: "+ResidentialCapacityCompatibility.Signature(key));
            foreach(object key in state.Keys)foreach(MethodInfo reader in readers)if(ResidentialCapacityHarmonyMetadata.IsDictionaryReaderFamily(key as MethodBase,reader))throw new InvalidOperationException("A provider family generic reader is patched.");
        }
        internal static Type Shared(Assembly[] assemblies)
        {Type shared=SharedOrNull(assemblies);if(shared==null)throw new InvalidOperationException("No generated CHSharedState exists.");return shared;}
        internal static Type SharedOrNull(Assembly[] assemblies)
        {
            Type shared=null;foreach(Assembly assembly in assemblies){Type candidate=assembly.GetType("CHSharedState",false);if(candidate==null)continue;if(shared!=null&&!ReferenceEquals(shared,candidate))throw new InvalidOperationException("Multiple CHSharedState types.");shared=candidate;}
            if(shared!=null&&(shared.TypeInitializer!=null||!shared.IsAbstract||!shared.IsSealed))throw new InvalidOperationException("A unique generated CHSharedState without cctor is required.");return shared;
        }
        internal static FieldInfo Field(Type type,string name,Type expected,bool isPrivate)
        {
            FieldInfo field=type.GetField(name,Static);
            if(field==null||field.FieldType!=expected||!field.IsStatic||!field.IsInitOnly||(isPrivate?!field.IsPrivate:!field.IsAssembly))throw new InvalidOperationException("Provider family field shape differs: "+type.FullName+"::"+name);return field;
        }
        internal static ResidentialCapacityProviderActivation Activate()
        {
            if(Type.GetType("Mono.Runtime",false)==null)throw new InvalidOperationException("Explicit provider family initialization is Mono-only.");
            Assembly[] loaded=AppDomain.CurrentDomain.GetAssemblies();Assembly[] assemblies=Select(loaded);Type shared=SharedOrNull(loaded);
            List<MethodBase> keys=new List<MethodBase>();List<MethodInfo> readers=new List<MethodInfo>();
            foreach(Assembly assembly in assemblies)
            {
                keys.AddRange(RequireMethods(assembly,ResidentialCapacityCanonicalIL.Material));
                readers.Add(assembly.GetType("HarmonyLib.GeneralExtensions",true).GetMethod("GetValueSafe",Static));
                Type processor=assembly.GetType("HarmonyLib.PatchProcessor",true),holder=assembly.GetType("HarmonyLib.HarmonySharedState",true);
                Field(processor,"locker",typeof(object),false);Field(holder,"state",typeof(Dictionary<MethodBase,byte[]>),true);Field(holder,"originals",typeof(Dictionary<MethodInfo,MethodBase>),true);Field(holder,"actualVersion",typeof(int),false);
            }
            // Before the first common state exists there can be no ordinary
            // publication in it. Bootstrap ONLY the pinned central cctor.
            // This is explicit activation, not a silent observation fallback.
            if(shared==null)
            {
                RuntimeHelpers.RunClassConstructor(assemblies[0].GetType("HarmonyLib.HarmonySharedState",true).TypeHandle);
                shared=Shared(AppDomain.CurrentDomain.GetAssemblies());
            }
            FieldInfo globalState=shared.GetField("state",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly),globalOriginals=shared.GetField("originals",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly),globalVersion=shared.GetField("version",BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly);
            if(globalState==null||globalState.FieldType!=typeof(Dictionary<MethodBase,byte[]>)||globalOriginals==null||globalOriginals.FieldType!=typeof(Dictionary<MethodInfo,MethodBase>)||globalVersion==null||globalVersion.FieldType!=typeof(int))throw new InvalidOperationException("Generated common-state shape differs.");
            object state=globalState.GetValue(null),originals=globalOriginals.GetValue(null);
            if(state==null||originals==null||!object.Equals(globalVersion.GetValue(null),102))throw new InvalidOperationException("Existing common-state initialization is incomplete.");
            List<ResidentialCapacityProviderActivation.Fork> forks=new List<ResidentialCapacityProviderActivation.Fork>();
            List<object> lockers=new List<object>();
            // This API explicitly initializes only reviewed provider cctors. It
            // MUST run outside all mod/engine/provider locks. Shared cctors can
            // initialize RuntimeDetour and subscribe compilation callbacks.
            // A state-only preflight never requests any other monitor/query.
            // Check cctor keys before explicitly executing even locker cctors.
            if(!System.Threading.Monitor.TryEnter(state))throw new InvalidOperationException("Common provider state is busy before explicit initialization.");
            try{RequireUnpatched((IDictionary)state,keys.ToArray(),readers.ToArray());}finally{System.Threading.Monitor.Exit(state);}
            foreach(Assembly assembly in assemblies)
            {
                MethodBase[] boundary=RequireMethods(assembly,ResidentialCapacityCanonicalIL.Material);
                Type processor=assembly.GetType("HarmonyLib.PatchProcessor",true),holder=assembly.GetType("HarmonyLib.HarmonySharedState",true);
                FieldInfo locker=Field(processor,"locker",typeof(object),false),privateState=Field(holder,"state",typeof(Dictionary<MethodBase,byte[]>),true),privateOriginals=Field(holder,"originals",typeof(Dictionary<MethodInfo,MethodBase>),true),version=Field(holder,"actualVersion",typeof(int),false);
                RuntimeHelpers.RunClassConstructor(processor.TypeHandle);object lockerValue=locker.GetValue(null);lockers.Add(lockerValue);
                MethodInfo reader=assembly.GetType("HarmonyLib.GeneralExtensions",true).GetMethod("GetValueSafe",Static);
                forks.Add(new ResidentialCapacityProviderActivation.Fork(assembly,locker,privateState,privateOriginals,version,lockerValue,reader,boundary));
            }
            ResidentialCapacityProviderLocks monitors;string reason;
            if(!ResidentialCapacityProviderLocks.TryAcquire(lockers.ToArray(),state,out monitors,out reason))throw new InvalidOperationException(reason);
            using(monitors)RequireUnpatched((IDictionary)state,keys.ToArray(),readers.ToArray());
            // Never hold provider/mod locks while a shared cctor runs.
            foreach(Assembly assembly in assemblies)RuntimeHelpers.RunClassConstructor(assembly.GetType("HarmonyLib.HarmonySharedState",true).TypeHandle);
            if(!ReferenceEquals(Shared(AppDomain.CurrentDomain.GetAssemblies()),shared))throw new InvalidOperationException("Initialization created/replaced a shared-state type.");
            ResidentialCapacityProviderActivation activation=new ResidentialCapacityProviderActivation(forks.ToArray(),shared,globalState,globalOriginals,globalVersion,state,originals);
            if(!activation.Validate(AppDomain.CurrentDomain.GetAssemblies(),out reason))throw new InvalidOperationException(reason);
            return activation;
        }
    }

    internal sealed class ResidentialCapacityPatchCertificate
    {
        private readonly Assembly[] _sources;
        private readonly Assembly _provider;
        private readonly Type _sharedType;
        private readonly FieldInfo _stateField, _originalsField, _versionField, _lockerField;
        private readonly object _locker, _state, _originals;
        private readonly ResidentialCapacityRegistrySnapshot _snapshot;
        private readonly ResidentialCapacityProviderActivation _activation;
        internal readonly ResidentialCapacityEffectiveConfiguration EffectiveConfiguration;
        internal int WatchedMethodCount { get { return _snapshot.Count; } }
        internal ResidentialCapacityPatchCertificate(ResidentialCapacityCompatibilityResult result, Assembly[] sources, Assembly provider, Type sharedType,
            FieldInfo stateField, FieldInfo originalsField, FieldInfo versionField, FieldInfo lockerField, object state, object originals, object locker,
            ResidentialCapacityRegistrySnapshot snapshot,ResidentialCapacityProviderActivation activation=null)
        {
            if (result == null || result.Kind != ResidentialCapacityCompatibilityKind.Known || !result.OriginalMethodsKnown || !result.EffectiveMetadataInspected || result.EffectiveConfiguration != ResidentialCapacityEffectiveConfiguration.OwnRawCapacityV1)
                throw new InvalidOperationException("Only a fully verified RAW configuration can produce a certificate.");
            _sources = (Assembly[])sources.Clone(); _provider = provider; _sharedType = sharedType; _stateField = stateField; _originalsField = originalsField; _versionField = versionField; _lockerField = lockerField;
            _state = state; _originals = originals; _locker = locker; _snapshot = snapshot; _activation=activation; EffectiveConfiguration = result.EffectiveConfiguration;
        }
        internal bool Revalidate(out string reason)
        {
            Assembly[] current = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly expected in _sources)
            {
                bool present = false;
                foreach (Assembly actual in current)
                    if (actual.GetName().Name == expected.GetName().Name)
                    { if (!ReferenceEquals(actual, expected)) { reason = "A covered source assembly was duplicated/replaced."; return false; } present = true; }
                if (!present) { reason = "A covered source assembly is absent."; return false; }
            }
            foreach (Assembly assembly in current)
            {
                Type harmony = assembly.GetType("HarmonyLib.Harmony", false);
                if (harmony != null && !ReferenceEquals(harmony.Assembly, _provider) && (_activation==null||!_activation.Allows(harmony.Assembly))) { reason = "Another Harmony2 registry appeared."; return false; }
                Type shared = assembly.GetType("CHSharedState", false);
                if (shared != null && !ReferenceEquals(shared, _sharedType)) { reason = "Another central provider shared-state type appeared."; return false; }
            }
            // All fields were shape/body-verified during full certification.
            // Re-reading their values neither hashes IL nor invokes a provider.
            if (!ReferenceEquals(_lockerField.GetValue(null), _locker) || !ReferenceEquals(_stateField.GetValue(null), _state) || !ReferenceEquals(_originalsField.GetValue(null), _originals) || !object.Equals(_versionField.GetValue(null), 102))
            { reason = "Provider synchronization/state identity changed."; return false; }
            if(_activation!=null&&!_activation.Validate(current,out reason))return false;
            ResidentialCapacityProviderLocks monitors;
            if(!ResidentialCapacityProviderLocks.TryAcquire(_activation==null?new[]{_locker}:_activation.Lockers,_state,out monitors,out reason))return false;
            using(monitors)
            {
                if (!ReferenceEquals(_lockerField.GetValue(null), _locker) || !ReferenceEquals(_stateField.GetValue(null), _state) || !ReferenceEquals(_originalsField.GetValue(null), _originals))
                { reason = "Provider state identity changed while validating."; return false; }
                if(_activation!=null&&!_activation.Validate(AppDomain.CurrentDomain.GetAssemblies(),out reason))return false;
                return _snapshot.Matches((IDictionary)_state, out reason);
            }
        }
    }

    // Reflection only. There is no Harmony reference/type in any signature,
    // field or initializer, no game method invocation, singleton/buffer access,
    // RNG draw, patch installation or activation decision.
    internal static class ResidentialCapacityCompatibility
    {
        internal const string SourceVersion = "CS1 Windows 1.21.1-f9";
        internal const string Coverage = "Pinned original managed IL/signatures and current CitiesHarmony.Harmony registry for HOME/formula/arrival boundaries; not arbitrary detours, external buffer mutation, generic allocator overrides, matched-family distribution or native object coherence.";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        internal sealed class MethodPin
        {
            internal readonly string Assembly, Type, Name, Signature, Il, Body;
            internal readonly int Length;
            internal MethodPin(string assembly, string type, string name, string signature, string il, string body, int length)
            { Assembly = assembly; Type = type; Name = name; Signature = signature; Il = il; Body = body; Length = length; }
        }
        private static readonly MethodPin[] Pins = new MethodPin[] {
            new MethodPin("Assembly-CSharp", "ResidentialBuildingAI", "CreateBuilding", "public instance System.Void ResidentialBuildingAI::CreateBuilding(System.UInt16,Building&)", "c892de97c4cbdbc8324813d5c6578abadd1c88eb27c639d9d84dc6a04d4e9ce9", "d7740303847f8efa56ca339cbcd627b11b2976220416835604819a1ee0754ad3", 71),
            new MethodPin("Assembly-CSharp", "ResidentialBuildingAI", "SimulationStepActive", "protected instance System.Void ResidentialBuildingAI::SimulationStepActive(System.UInt16,Building&,Building+Frame&)", "8a589876f7cc177d0d9c63c54fac11bbb9bc8d8a2a14c64fb01601e745e02b2a", "7e9b865d9651cf470f35de5f7350e0341168630f22d3e7dbcf00f659ae49b1d9", 3139),
            new MethodPin("Assembly-CSharp", "ResidentialBuildingAI", "GetHomeReason", "private instance TransferManager+TransferReason ResidentialBuildingAI::GetHomeReason(System.UInt16,Building&,ColossalFramework.Math.Randomizer&)", "c1b28cf53c110e00dd789678715174beec766702217bbfed1eef4f9517125bc1", "f9a8b3885737492d68542cdecafeb48ac4df4890cfebb50b6a30d824f1194c9a", 239),
            new MethodPin("Assembly-CSharp", "ResidentialBuildingAI", "CalculateHomeCount", "public instance System.Int32 ResidentialBuildingAI::CalculateHomeCount(ItemClass+Level,ColossalFramework.Math.Randomizer,System.Int32,System.Int32)", "f020886a214b4a98ff3a1ad0a86efae08cd3ebeb7b877c0ac52893a21ea7d0a6", "0451c7b4c0f5faf6b7f86fa38490734e23ff59d1b633ff073a562461d35754eb", 199),
            new MethodPin("Assembly-CSharp", "PrivateBuildingAI", "CreateBuilding", "public instance System.Void PrivateBuildingAI::CreateBuilding(System.UInt16,Building&)", "f5b625e4b53598002e99f4396c92696bcf6e68870eec06d38c9c1212c89e1043", "85d97b36cd458cf0cb6552534f5b8f7b74302dcefb389f26178b1384dd1692ee", 197),
            new MethodPin("Assembly-CSharp", "PrivateBuildingAI", "BuildingLoaded", "public instance System.Void PrivateBuildingAI::BuildingLoaded(System.UInt16,Building&,System.UInt32)", "951020064677590515dea91bbf344875b2b4e3ba628d5cafaf81974b24639728", "d8021fc0fa0cfc5bb0e36f4bacdebe9e8d31027e0918c2a4b7a0eeb42c0ae397", 187),
            new MethodPin("Assembly-CSharp", "PrivateBuildingAI", "BuildingUpgraded", "public instance System.Void PrivateBuildingAI::BuildingUpgraded(System.UInt16,Building&)", "b3d2ed7a8bc397a516b3ccf5aadb020dcc3a99937931ea2e93e73160ecb689d5", "f2962bbecae3ce9dd9f7d785ee9c8c48a85e20633cf5f5d7e299aef72dffa570", 178),
            new MethodPin("Assembly-CSharp", "CommonBuildingAI", "CreateBuilding", "public instance System.Void CommonBuildingAI::CreateBuilding(System.UInt16,Building&)", "1da7f13d8a3a7b868152c945b832e8392634948192a78674770b7ca8c4620efb", "a5842fde7cb9fe76d9ed740b965365e87ed88823e277b0683b03c7b4976bee36", 52),
            new MethodPin("Assembly-CSharp", "CommonBuildingAI", "BuildingLoaded", "public instance System.Void CommonBuildingAI::BuildingLoaded(System.UInt16,Building&,System.UInt32)", "93d3c7b3f612c7b7af549709c50cf8cc858efcbf61c404875145ac7f6b3d43a6", "ab2eb7d3b8b37f9d37e20b53f6765035e4912e74407c7221ce48b47b344bd0ea", 38),
            new MethodPin("Assembly-CSharp", "CommonBuildingAI", "GetHomeBehaviour", "protected instance System.Void CommonBuildingAI::GetHomeBehaviour(System.UInt16,Building&,Citizen+BehaviourData&,System.Int32&,System.Int32&,System.Int32&,System.Int32&,System.Int32&)", "bd9ab78cbe09233e3a163611962a3e52ff0c1634b6cf6ed366eb0d8846d043fc", "40f019d6bcabc6b47a2aeb280b0fdb56d2f8aab14a2dbf9fe797bdab90b597fe", 216),
            new MethodPin("Assembly-CSharp", "BuildingAI", "CreateBuilding", "public instance System.Void BuildingAI::CreateBuilding(System.UInt16,Building&)", "dc16d9437b6d8f113e54eb760c059b17027c19d1be4694d0955ee46c2b7b7a69", "27873c77b6c0b33e12c97891e92dd030612d2eab6d726ff76335e871a40d417c", 19),
            new MethodPin("Assembly-CSharp", "BuildingAI", "BuildingLoaded", "public instance System.Void BuildingAI::BuildingLoaded(System.UInt16,Building&,System.UInt32)", "684888c0ebb17f374298b65ee2807526c066094c701bcc7ebbe1c1095f494fc1", "90b6b1cdcdaa924e7014eb1ebfb3e1f38d03d78a59374505ed824e7e3be4c4e4", 1),
            new MethodPin("Assembly-CSharp", "BuildingAI", "EnsureCitizenUnits", "protected instance System.Void BuildingAI::EnsureCitizenUnits(System.UInt16,Building&,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)", "d2651cade7903dc6a97b2fd9bc3ca686670430fbc1fd7865cc7471c19b33e225", "281d9324236b4ad29db542f6d9481fdbaeecb59231a18b8c4f6485e48419115b", 412),
            new MethodPin("Assembly-CSharp", "CitizenManager", "CreateUnits", "public instance System.Boolean CitizenManager::CreateUnits(System.UInt32&,ColossalFramework.Math.Randomizer&,System.UInt16,System.UInt16,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)", "9ebbc0164f3338bbc2aaf5b44a0afe2ddaae35026f3bd42321ca1744420da8d1", "ce9badcdfd328876201e118f1a5aae28a3d80969616acbb0299ed0afaf0c33c2", 549),
            new MethodPin("Assembly-CSharp", "CitizenManager", "ReleaseUnits", "public instance System.Void CitizenManager::ReleaseUnits(System.UInt32)", "d6def5b0f6e0d57f8a28f1bff526a0dd9e64afdab9cfe67606c736883f897370", "7ee8b3a4fd348b3c33045ca9a2179b24b94bddca66db3d9e6e6713193bbfe959", 126),
            new MethodPin("Assembly-CSharp", "CitizenManager", "ReleaseUnitImplementation", "private instance System.Void CitizenManager::ReleaseUnitImplementation(System.UInt32,CitizenUnit&)", "a70782edbc92a2c852eaa4febccefabadb3c04135065057a3f003cc07847b9c2", "78b05e2a5a7ab7ac9ff1de1628897e8263a6bba21e021d9c0879a9aa38096ee7", 90),
            new MethodPin("Assembly-CSharp", "CitizenManager", "ReleaseUnitCitizen", "private instance System.Void CitizenManager::ReleaseUnitCitizen(System.UInt32,CitizenUnit&,System.UInt32)", "8bf36693316e9a7ecf3c9df5b5a38f7d786b0f0bc0d84fdf5ec1620d58ffd95b", "55febba6e3cb2b48a802b3994ab4210119cd0b35b82f37aa857a4cd6d504c0e4", 211),
            new MethodPin("Assembly-CSharp", "CitizenManager", "CreateCitizen", "public instance System.Boolean CitizenManager::CreateCitizen(System.UInt32&,System.Int32,System.Int32,ColossalFramework.Math.Randomizer&)", "4aae722ffd8a403d3f96984b379a480b90fa7d1952e3659c3ad0740ed64fdf1b", "3ccb29546c7837865f83e0d19bf169a51d39809390ac8581ec58efad26755d11", 125),
            new MethodPin("Assembly-CSharp", "CitizenManager", "CreateCitizen", "public instance System.Boolean CitizenManager::CreateCitizen(System.UInt32&,System.Int32,System.Int32,ColossalFramework.Math.Randomizer&,Citizen+Gender)", "93b6676be4f74a2cfee3f2d2e76478f15abce5dad8d9de314cd3d865f79e9e7f", "48d9ed2c4225b88ddc25ed5fa93fa2047d0d0b63b40af0c93c4574700a8b13b0", 194),
            new MethodPin("Assembly-CSharp", "BuildingManager", "CreateBuilding", "public instance System.Boolean BuildingManager::CreateBuilding(System.UInt16&,ColossalFramework.Math.Randomizer&,BuildingInfo,UnityEngine.Vector3,System.Single,System.Int32,System.UInt32)", "a155365a2f77048ba7368392f0722f532c117be6425ba5f680788c50ad0aec42", "8e78d6e857f8f135b5503410c147f26c02dffee8b92d4959836ea9c0fe691b2b", 2124),
            new MethodPin("Assembly-CSharp", "BuildingManager", "UpdateBuildingInfo", "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,BuildingInfo)", "ba50bcd68384670a7bde8d499ef61c08d0c6ab154eb117e7164c2f078fda780f", "8c5c61e1be39b1a3431657440d25f3c11e0eac23a2de5faf889e98566f2fc5b0", 26),
            new MethodPin("Assembly-CSharp", "BuildingManager", "UpdateBuildingInfo", "public instance System.Void BuildingManager::UpdateBuildingInfo(System.UInt16,Building&,BuildingInfo)", "957fe9ed636ac968b95c56656f1b819dea529717a8a042b5e709cdab7e4899d0", "f5e798d02b9822ce0a69d6afba6e93ef02681fcc3af33a27a8d0fbe513fc9824", 938),
            new MethodPin("Assembly-CSharp", "Building", "get_Width", "public instance System.Int32 Building::get_Width()", "9a4bbab9ba534ae96e0bb584879cc26fbbcc3e2888de97898042c8833cad0556", "8aa417840fd60e5e40ac73bf67b8eeb4159cda48256c7e2d592e10e933172737", 7),
            new MethodPin("Assembly-CSharp", "Building", "get_Length", "public instance System.Int32 Building::get_Length()", "46cacb3890ecb0ab4e3c1d24b9908b4429075e5580e553fe2ab9c4937e7b0c9f", "f60879dce363d198dcc7b796e0953b72c02f02dc59aa1f435e9aa827ed2c6892", 7),
            new MethodPin("Assembly-CSharp", "Building", "GetEmptyCitizenUnit", "public instance System.UInt32 Building::GetEmptyCitizenUnit(CitizenUnit+Flags)", "eb9400777c22703ae4193a07c1c9bc7cf40907e41e807ff4883ad118eb4ff5cd", "ae624cb0a14c13d77891aad22c7e5d8bc5a21b869cddc1bd6729f51f7818c2fa", 156),
            new MethodPin("Assembly-CSharp", "Citizen", "SetHome", "public instance System.Void Citizen::SetHome(System.UInt32,System.UInt16,System.UInt32)", "5e69284864c245a0d65529c87a8b0d0190fee8e594791eeb5cd8723cf5f7c194", "86ec8e0f306909970aa911f9ca062744e82615311c9bd6a58290075593a9a0dc", 295),
            new MethodPin("Assembly-CSharp", "Citizen", "AddToUnits", "public instance System.Boolean Citizen::AddToUnits(System.UInt32,System.UInt32,CitizenUnit+Flags)", "45a04d528f31f5289b080666a43252d3987641ffd0fc38fdcad7993838859d25", "e890217fef017f038098be16be98fc0c46e59c6318b26599ee9b97198afc363c", 152),
            new MethodPin("Assembly-CSharp", "Citizen", "AddToUnit", "public instance System.Boolean Citizen::AddToUnit(System.UInt32,CitizenUnit&)", "413512c2b0eb41218e94b65e243c1b6b7c63e08a447d95b9a5c584adbc9a1b66", "dec269bf40c28f30f92209b97eb07934a9deea030f023a9e1508fc64f2e3c3e1", 102),
            new MethodPin("Assembly-CSharp", "CitizenUnit", "SetWealthLevel", "public instance System.Void CitizenUnit::SetWealthLevel(Citizen+Wealth)", "691bfaf90b70ed307dd2f3141c63759a65fec1f2b228f110f167448e33367c85", "13db0d5956a67bf9dc526af8386d01fd51b051e2c334e10ce1846d7de2d2add4", 182),
            new MethodPin("Assembly-CSharp", "CitizenUnit", "Empty", "public instance System.Boolean CitizenUnit::Empty()", "1382cd100d5577869cdf91694c5680f0f591c2071bc76bd47c95f70151391e85", "c198ab6c61683f724e42c271728add2f82858791444e39eedc4ad79fbe0ca5e0", 67),
            new MethodPin("Assembly-CSharp", "CitizenUnit", "Full", "public instance System.Boolean CitizenUnit::Full()", "5b4d1cbc6431b2ae25ff4cf6ca39fc3fcfc50fcce6676059f140251437821591", "18c2b1e6efde63604b994f5094dd9455103bc3806da77fcc469e2d019da25e72", 67),
            new MethodPin("Assembly-CSharp", "OutsideConnectionAI", "StartTransfer", "public instance System.Void OutsideConnectionAI::StartTransfer(System.UInt16,Building&,TransferManager+TransferReason,TransferManager+TransferOffer)", "7996a733785da09fddf3ea103f061b48af86bc6d545e9a5258bfd80702c1a66d", "dd4e811791b957f131ef8c81c3cdc6082be2421c56d79c9b5b45a851ee30cb56", 45),
            new MethodPin("Assembly-CSharp", "OutsideConnectionAI", "StartConnectionTransfer", "public static System.Boolean OutsideConnectionAI::StartConnectionTransfer(System.UInt16,Building&,TransferManager+TransferReason,TransferManager+TransferOffer,System.Int32,System.Int32,System.Int32)", "0297aa96a8d51aa0662046555fb26664a776985ed688623351a1a39bc0a2c5ec", "c18bd11dce7c6069e8eb00c498d84201a516d266b798769b89280e507d8948a8", 56),
            new MethodPin("Assembly-CSharp", "OutsideConnectionAI", "StartConnectionTransferImpl", "private static System.Boolean OutsideConnectionAI::StartConnectionTransferImpl(System.UInt16,Building&,TransferManager+TransferReason,TransferManager+TransferOffer,System.Int32,System.Int32,System.Int32)", "ef0f7ac9a8740df47b6f888a08ed154ba1c1c77a132068a78ba6b17763e7113c", "d832dafe09b760105ee60faa12bd80d7261e394e98611a8c65ece9dc236664ad", 4602),
            new MethodPin("Assembly-CSharp", "ItemClass", "get_isResidentialLowGeneric", "public instance System.Boolean ItemClass::get_isResidentialLowGeneric()", "dd0848df352735254b5aa1ca0c78d2891f8b8dfc721d6bfda22403f704a5a6d5", "1eba3d013a7b3c69d5dfca42cbd89a246bc8f515d3782d8267abd4ee47b18e86", 10),
            new MethodPin("Assembly-CSharp", "ItemClass", "get_isResidentialLowEco", "public instance System.Boolean ItemClass::get_isResidentialLowEco()", "d3357441e59c833b7f201f95559d97568d0342ca6d13b7acbfe25a5fc68b28f4", "a9754ffd97791969dcf672c06ac35031547ff952799e6c4b91c6f07782852bbd", 11),
            new MethodPin("ColossalManaged", "ColossalFramework.Math.Randomizer", ".ctor", "public instance System.Void ColossalFramework.Math.Randomizer::.ctor(System.Int32)", "c71efa2602c790390c642f56ca182cc6e77a6b2887470af9fe2081dc9bf18b20", "925c9dfd6abf55270ea6f0bec23bab176e311b73ca7015ad4c556b1e3353f514", 29),
            new MethodPin("ColossalManaged", "ColossalFramework.Math.Randomizer", ".ctor", "public instance System.Void ColossalFramework.Math.Randomizer::.ctor(System.UInt32)", "39e20825819c47b09bdb5fd3bcab6abb7dfcf1a45e6d1e91e42c75f7c03dbfbe", "57d607e013c8161f9bddeac0ebbb4992f17d008f9b359d4360a09ab721beed96", 29),
            new MethodPin("ColossalManaged", "ColossalFramework.Math.Randomizer", "Int32", "public instance System.Int32 ColossalFramework.Math.Randomizer::Int32(System.UInt32)", "030eb9e4e68771862a7a801a2afc722fd281b0893570bfef1ce8c8fbaa4e76e7", "58e24541286375bc483cf829a64a4a845e1e7e50a6e50d9a1e82713e9f474d79", 49),
            new MethodPin("ColossalManaged", "ColossalFramework.Math.Randomizer", "Int32", "public instance System.Int32 ColossalFramework.Math.Randomizer::Int32(System.Int32,System.Int32)", "0af16a0a9614b484d452dc8360392124ad2ef84d4b47d13ec1de78a6e18f7911", "fff5f8c24f365bda16f55bc4ed800d626a5a0be198b7e70f05cc4bb62173bd5e", 55),
            new MethodPin("Assembly-CSharp", "Array32`1", "CreateItem", "public instance System.Boolean Array32`1::CreateItem(System.UInt32&)", "7e139198d6b329ead72a44fd950d13cb6e579bd6fd41662841cceb1411a2f6c7", "77489810add951257c543c4443435727ddc5b7a4608d0f844a4f371167852fdf", 45),
            new MethodPin("Assembly-CSharp", "Array32`1", "CreateItem", "public instance System.Boolean Array32`1::CreateItem(System.UInt32&,ColossalFramework.Math.Randomizer&)", "4874022dfe6dd0de4dcad30d627cdc0554dbdeed874ab84731ba0c6a617811af", "e6a62135e7f9828d71fd7b366918741cc2dcb71f28b9b083bcddd07252d05a00", 74),
            new MethodPin("Assembly-CSharp", "Array32`1", "ReleaseItem", "public instance System.Void Array32`1::ReleaseItem(System.UInt32)", "9390a97820230b0cfa0f517df21bc8846682ac9d30ffc2e8cfb10fc9b1a04804", "c3c1a94b45b922815899143ce0095b287eb2d01a2d1ef045f72f9181313e5623", 27),
            new MethodPin("UnityEngine", "UnityEngine.Mathf", "Max", "public static System.Int32 UnityEngine.Mathf::Max(System.Int32,System.Int32)", "aae2e3b14119f159baa559d7be668aef071014d28ae0f8f607357a2ed501352a", "5f16c2d06a26b63667c049f4c957acf61ca9b822094eac0f1766f8f6dc898aeb", 23),
        };
        private static readonly MethodPin[] AdmissionPins = new MethodPin[] {
            new MethodPin("Assembly-CSharp", "ZoneBlock", "SimulationStep", "public instance System.Void ZoneBlock::SimulationStep(System.UInt16)", "1cbce958ca74423d28820298c8c36aa5f900158800c55aa1df981871ea353b97", "01e04c0d1ab5d75122fe9a2ab3e5a90a914e44c5661334c25b106c7b409d469a", 4188),
            new MethodPin("Assembly-CSharp", "PrivateBuildingAI", "SimulationStep", "public instance System.Void PrivateBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "5c0705f1f350f8baf92dc9646e37e64ebd8ea687dfde743a44aea199c48f65a9", "21927f06aa3b46299ecc894511714a1613258e4cd76cd19fe33f49b55bb88eaf", 1839),
            new MethodPin("Assembly-CSharp", "ResidentialBuildingAI", "SimulationStep", "public instance System.Void ResidentialBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)", "250074cf0c7ab5ae2b412102b03809d7476642073a799bf21f341101617316cf", "f786a377a34ec65fa08ab2a0a3061a9a1f448952c5fef2c7335301347b5c98a1", 807),
            new MethodPin("Assembly-CSharp", "BuildingManager", "ReleaseBuilding", "public instance System.Void BuildingManager::ReleaseBuilding(System.UInt16)", "9f85239cef7329c14ddd54f06c698b7acd2631b03c5dcebab5350ada512f1cde", "6b56c07c5fc8c3c2a406729197584cf7414b95b05543563c1e0cc9d92b211e91", 55),
            new MethodPin("Assembly-CSharp", "PrivateBuildingAI", "StartUpgrading", "public instance System.Void PrivateBuildingAI::StartUpgrading(System.UInt16,Building&)", "0ed985bd4e986c7c44579c36c976614f18831757142293f3e038b431690e0eaa", "d372c29a65ef963d05b9935c6986142507ebd2534aee1886d175c60ce9843b36", 397)
        };

        internal static ResidentialCapacityCompatibilityResult ObserveOriginal()
        { return ObserveOriginalMetadata(AppDomain.CurrentDomain.GetAssemblies(), "LoadedOriginalMetadata"); }

        // Data-only verification entry; never used to certify runtime patches.
        internal static ResidentialCapacityCompatibilityResult ObserveOriginalMetadata(Assembly[] assemblies, string scope)
        {
            List<ResidentialCapacityMethodObservation> observations = new List<ResidentialCapacityMethodObservation>();
            try
            {
                Dictionary<string, Assembly> found = SelectAssemblies(assemblies);
                RequireModule(found, "Assembly-CSharp", "e8d46364-9995-4176-87e0-ae3192b9a20b");
                RequireModule(found, "ColossalManaged", "9ba4048a-69aa-454a-abc4-c1c47e9b3e86");
                RequireModule(found, "UnityEngine", "70f94622-f0ee-40e4-ba11-dc128c0be4a6");
                Type build = found["Assembly-CSharp"].GetType("BuildConfig", true);
                RequireConstant(build, "APPLICATION_VERSION", 222554384u);
                RequireConstant(build, "APPLICATION_VERSION_A", 1u); RequireConstant(build, "APPLICATION_VERSION_B", 21u);
                RequireConstant(build, "APPLICATION_VERSION_C", 1u); RequireConstant(build, "APPLICATION_RELEASE_TYPE", 15u);
                RequireConstant(build, "APPLICATION_BUILD_NUMBER", 9u); RequireConstant(build, "DATA_FORMAT_VERSION", 121034u);
                bool known = true;
                foreach (MethodPin pin in Pins)
                {
                    ResidentialCapacityMethodObservation observation = ObserveMethod(Resolve(found, pin), pin);
                    observations.Add(observation); known &= observation.Known;
                }
                return Result(known, scope, known ? "Pinned original method metadata matches; effective patches were not inspected." : "One or more pinned original bodies differ.", known, false, observations, null);
            }
            catch (Exception error) { return Result(false, scope, Failure(error), false, false, observations, null); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult CertifyForControl()
        { ResidentialCapacityPatchCertificate ignored; return CertifyForControlCore(false, null, out ignored); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult CertifyForControl(out ResidentialCapacityPatchCertificate certificate)
        { return CertifyForControlCore(true, null, out certificate); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryActivateOfficialProviderCopies(out ResidentialCapacityProviderActivation activation,out string reason)
        {
            activation=null;
            try{activation=ResidentialCapacityProviderFamily.Activate();reason=null;return true;}
            catch(Exception error){reason=Failure(error);return false;}
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult CertifyForControl(ResidentialCapacityProviderActivation activation,out ResidentialCapacityPatchCertificate certificate)
        {return CertifyForControlCore(true,activation,out certificate);}
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ResidentialCapacityCompatibilityResult CertifyForControlCore(bool raw, ResidentialCapacityProviderActivation activation, out ResidentialCapacityPatchCertificate certificate)
        {
            certificate = null;
            ResidentialCapacityCompatibilityResult original = ObserveOriginal();
            if (original.Kind != ResidentialCapacityCompatibilityKind.Known) return original;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (raw) original = ObserveAdmissionMetadata(original, assemblies);
            if (original.Kind != ResidentialCapacityCompatibilityKind.Known) return original;
            try { return ResidentialCapacityHarmonyMetadata.Inspect(original, assemblies, raw, activation, out certificate); }
            catch (Exception error) { return new ResidentialCapacityCompatibilityResult(false, "EffectiveHarmonyMetadata", Failure(error), true, false, original.Methods, new ResidentialCapacityPatchObservation[0]); }
        }
        internal static ResidentialCapacityCompatibilityResult ObserveAdmissionMetadata(ResidentialCapacityCompatibilityResult original, Assembly[] assemblies)
        {
            if (original == null) throw new ArgumentNullException("original");
            List<ResidentialCapacityMethodObservation> observations = new List<ResidentialCapacityMethodObservation>(original.Methods);
            try
            {
                Dictionary<string, Assembly> found = SelectAssemblies(assemblies); bool known = original.Kind == ResidentialCapacityCompatibilityKind.Known && original.OriginalMethodsKnown;
                foreach (MethodPin pin in AdmissionPins) { ResidentialCapacityMethodObservation observation = ObserveMethod(Resolve(found, pin), pin); observations.Add(observation); known &= observation.Known; }
                return Result(known, "OriginalAdmissionMetadata", known ? "Original HOME/arrival and five admission/mutation method bodies match; no effective certificate." : "Original admission metadata differs.", known, false, observations, null);
            }
            catch (Exception error) { return Result(false, "OriginalAdmissionMetadata", Failure(error), false, false, observations, null); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool RevalidateForControl(ResidentialCapacityPatchCertificate certificate, out string reason)
        {
            if (certificate == null || certificate.EffectiveConfiguration != ResidentialCapacityEffectiveConfiguration.OwnRawCapacityV1)
            { reason = "A verified RAW certificate is required."; return false; }
            try { return certificate.Revalidate(out reason); }
            catch (Exception error) { reason = Failure(error); return false; }
        }

        // Only these concrete boundary methods are queried. Generic Array32<T>
        // methods are original-IL observations, not an attempt to certify every
        // possible closed generic patch/allocator mutation in the game.
        internal static MethodBase[] EffectiveTargets(Assembly[] assemblies)
        { return EffectiveTargets(assemblies, false); }
        internal static MethodBase[] EffectiveTargets(Assembly[] assemblies, bool includeAdmission)
        {
            Dictionary<string, Assembly> found = SelectAssemblies(assemblies); List<MethodBase> result = new List<MethodBase>();
            foreach (MethodPin pin in Pins) if (pin.Type != "Array32`1") result.Add(Resolve(found, pin));
            if (includeAdmission) foreach (MethodPin pin in AdmissionPins) result.Add(Resolve(found, pin));
            return result.ToArray();
        }

        internal static ResidentialCapacityCompatibilityResult EvaluatePatches(ResidentialCapacityCompatibilityResult original,
            ResidentialCapacityPatchObservation[] patches, bool ownDependenciesKnown = false)
        {
            if (original == null || patches == null) throw new ArgumentNullException("original");
            bool originalKnown = original.Kind == ResidentialCapacityCompatibilityKind.Known && original.OriginalMethodsKnown;
            bool[] seen = new bool[5]; string rejection = null;
            foreach (ResidentialCapacityPatchObservation patch in patches)
            {
                int match = ResidentialCapacityOwnHooks.Match(patch);
                if (match < 0) { rejection = patch == null ? "Null effective patch." : "Unapproved effective patch: " + patch.Target + " | " + patch.Kind + " | " + patch.Owner + " | " + patch.Hook; break; }
                if (seen[match]) { rejection = "Duplicate approved own hook: " + patch.Hook; break; } seen[match] = true;
            }
            bool own = patches.Length == 5 && rejection == null && ownDependenciesKnown;
            for (int i = 0; i < seen.Length; i++) own &= seen[i];
            bool clear = originalKnown && (patches.Length == 0 || own);
            string reason = !originalKnown ? "Original metadata is not known; patch metadata cannot certify it." :
                clear ? (own ? "Exact five covered own hooks and their reviewed managed dependencies match; separate growth gates/session remain outside this certificate." : "Original-only covered registry matches; this does not certify the own-hook configuration or authorize gameplay.") :
                rejection ?? (patches.Length != 5 ? "The own-hook configuration requires exactly all five covered hooks." : "Own hook/helper bodies have not been verified.");
            return new ResidentialCapacityCompatibilityResult(clear, "EffectiveHarmonyMetadata", reason, original.OriginalMethodsKnown, true, original.Methods, patches,
                own && clear ? ResidentialCapacityEffectiveConfiguration.OwnLotClassV1 : ResidentialCapacityEffectiveConfiguration.OriginalOnly);
        }

        internal static ResidentialCapacityCompatibilityResult EvaluateRawPatches(ResidentialCapacityCompatibilityResult original,
            ResidentialCapacityPatchObservation[] patches, bool ownDependenciesKnown)
        {
            if (original == null || patches == null) throw new ArgumentNullException("original");
            bool[] seen = new bool[23]; string rejection = null;
            foreach (ResidentialCapacityPatchObservation patch in patches)
            {
                int match = ResidentialCapacityRawHooks.Match(patch);
                if(match < 0) { rejection=patch==null?"Null RAW patch descriptor.":"Unapproved RAW effective patch or ordering: "+patch.Target+" | "+patch.Kind+" | "+patch.Owner+" | "+patch.Hook;break; }
                if(seen[match]) { rejection="Duplicate RAW hook: "+patch.Hook;break; }seen[match]=true;
            }
            bool known=original.Kind==ResidentialCapacityCompatibilityKind.Known&&original.OriginalMethodsKnown&&original.Methods.Length==49&&patches.Length==23&&ownDependenciesKnown&&rejection==null;
            for(int i=0;i<seen.Length;i++)known&=seen[i];
            string reason=known?"Exact RAW 23-hook/46-target configuration and reviewed helper bodies match; revalidate the certificate at every spend boundary.":rejection??"Complete RAW hook/body/original metadata is required.";
            return new ResidentialCapacityCompatibilityResult(known,"EffectiveRawHarmonyMetadata",reason,original.OriginalMethodsKnown,true,original.Methods,patches,ResidentialCapacityEffectiveConfiguration.OwnRawCapacityV1);
        }

        private static Dictionary<string, Assembly> SelectAssemblies(Assembly[] assemblies)
        {
            if (assemblies == null) throw new ArgumentNullException("assemblies");
            Dictionary<string, Assembly> result = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (Assembly a in assemblies)
            {
                string name = a.GetName().Name;
                if (name != "Assembly-CSharp" && name != "ColossalManaged" && name != "UnityEngine") continue;
                if (result.ContainsKey(name) && !ReferenceEquals(result[name], a)) throw new InvalidOperationException("Duplicate loaded source assembly: " + name);
                result[name] = a;
            }
            return result;
        }
        private static void RequireModule(Dictionary<string, Assembly> assemblies, string name, string mvid)
        {
            Assembly value;
            if (!assemblies.TryGetValue(name, out value) || value.ManifestModule.ModuleVersionId != new Guid(mvid))
                throw new InvalidOperationException("Required source assembly absent or module differs: " + name);
        }
        private static void RequireConstant(Type type, string name, uint expected)
        {
            FieldInfo field = type.GetField(name, All);
            if (field == null || !field.IsLiteral || Convert.ToUInt32(field.GetRawConstantValue(), CultureInfo.InvariantCulture) != expected)
                throw new InvalidOperationException("Native version constant differs: " + name);
        }
        private static MethodBase Resolve(Dictionary<string, Assembly> assemblies, MethodPin pin)
        {
            Type type = assemblies[pin.Assembly].GetType(pin.Type, true); MethodBase selected = null;
            MethodBase[] candidates = pin.Name == ".ctor" ? (MethodBase[])type.GetConstructors(All) : (MethodBase[])type.GetMethods(All);
            foreach (MethodBase candidate in candidates) if (candidate.Name == pin.Name && Signature(candidate) == pin.Signature)
            { if (selected != null) throw new AmbiguousMatchException(pin.Signature); selected = candidate; }
            if (selected == null) throw new MissingMethodException(pin.Signature);
            return selected;
        }
        internal static ResidentialCapacityMethodObservation ObserveMethod(MethodBase method, MethodPin pin)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return new ResidentialCapacityMethodObservation(pin.Signature, null, null, false, "Managed body absent.");
            byte[] il = body.GetILAsByteArray(); string ilHash = Hash(il), bodyHash = Hash(Encoding.UTF8.GetBytes(BodyMaterial(method)));
            bool known = Signature(method) == pin.Signature && il.Length == pin.Length && ilHash == pin.Il && bodyHash == pin.Body;
            return new ResidentialCapacityMethodObservation(pin.Signature, ilHash, bodyHash, known, known ? null : "Signature/IL/locals/EH fingerprint differs.");
        }
        internal static string Signature(MethodBase method)
        {
            List<string> parameters = new List<string>(); foreach (ParameterInfo parameter in method.GetParameters()) parameters.Add(parameter.ParameterType.FullName);
            MethodInfo info = method as MethodInfo;
            return (method.IsPublic ? "public" : method.IsFamily ? "protected" : method.IsPrivate ? "private" : "other") + " " +
                (method.IsStatic ? "static" : "instance") + " " + (info == null ? "System.Void" : info.ReturnType.FullName) + " " +
                method.DeclaringType.FullName + "::" + method.Name + "(" + string.Join(",", parameters.ToArray()) + ")";
        }
        internal static string BodyMaterial(MethodBase method)
        {
            MethodBody body = method.GetMethodBody(); StringBuilder s = new StringBuilder();
            s.Append("ILBodyV1;maxStack=").Append(body.MaxStackSize.ToString(CultureInfo.InvariantCulture)).Append(";initLocals=").Append(body.InitLocals ? "1" : "0")
                .Append(";IL=").Append(Hex(body.GetILAsByteArray())).Append(";locals=");
            foreach (LocalVariableInfo local in body.LocalVariables) s.Append(local.LocalIndex.ToString(CultureInfo.InvariantCulture)).Append(':').Append(local.LocalType.FullName).Append(':').Append(local.IsPinned ? "1" : "0").Append(';');
            s.Append("EH=");
            foreach (ExceptionHandlingClause e in body.ExceptionHandlingClauses)
            {
                s.Append(((int)e.Flags).ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.TryOffset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.TryLength.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(e.HandlerOffset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.HandlerLength.ToString(CultureInfo.InvariantCulture)).Append(':');
                if (e.Flags == ExceptionHandlingClauseOptions.Clause) s.Append(e.CatchType == null ? null : e.CatchType.FullName);
                else if (e.Flags == ExceptionHandlingClauseOptions.Filter) s.Append(e.FilterOffset.ToString(CultureInfo.InvariantCulture));
                s.Append(';');
            }
            return s.ToString();
        }
        internal static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }
        internal static string Failure(Exception error) { return error.GetType().Name + ": " + error.Message; }
        private static ResidentialCapacityCompatibilityResult Result(bool known, string scope, string reason, bool original, bool effective,
            List<ResidentialCapacityMethodObservation> methods, List<ResidentialCapacityPatchObservation> patches)
        { return new ResidentialCapacityCompatibilityResult(known, scope, reason, original, effective, methods.ToArray(), patches == null ? new ResidentialCapacityPatchObservation[0] : patches.ToArray()); }
    }

    // Optional effective-metadata inspection is isolated from Shadow observation.
    // Only already-loaded assemblies are used; nothing installs/loads a provider.
    internal static class ResidentialCapacityHarmonyMetadata
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult Inspect(ResidentialCapacityCompatibilityResult original, Assembly[] assemblies)
        { ResidentialCapacityPatchCertificate ignored; return Inspect(original, assemblies, false, out ignored); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult Inspect(ResidentialCapacityCompatibilityResult original, Assembly[] assemblies, out ResidentialCapacityPatchCertificate certificate)
        { return Inspect(original, assemblies, true, out certificate); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult Inspect(ResidentialCapacityCompatibilityResult original, Assembly[] assemblies, bool raw, out ResidentialCapacityPatchCertificate certificate)
        {return Inspect(original,assemblies,raw,null,out certificate);}
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ResidentialCapacityCompatibilityResult Inspect(ResidentialCapacityCompatibilityResult original, Assembly[] assemblies, bool raw, ResidentialCapacityProviderActivation activation, out ResidentialCapacityPatchCertificate certificate)
        {
            certificate = null;
            if (Type.GetType("Mono.Runtime", false) == null)
                throw new InvalidOperationException("Effective provider metadata is supported only in the game's Mono runtime; do not initialize this legacy provider on another CLR.");
            Assembly provider = null;
            foreach (Assembly assembly in assemblies) if (assembly.GetName().Name == "CitiesHarmony.Harmony")
            { if (provider != null && !ReferenceEquals(provider, assembly)) throw new InvalidOperationException("Duplicate provider assemblies."); provider = assembly; }
            if (provider == null || provider.ManifestModule.ModuleVersionId != new Guid("5de708ea-59c8-4f69-a6f5-3f97631a387f"))
                throw new InvalidOperationException("The verified central provider is absent or has a different module.");
            Type harmony = provider.GetType("HarmonyLib.Harmony", true);
            foreach (Assembly assembly in assemblies)
            {
                Type other = assembly.GetType("HarmonyLib.Harmony", false);
                if (other != null && !ReferenceEquals(other.Assembly, provider) && (activation==null||!activation.Allows(other.Assembly))) throw new InvalidOperationException("Another Harmony2 metadata registry is loaded: " + other.Assembly.FullName);
            }
            MethodInfo query = harmony.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(MethodBase) }, null);
            if (query == null || ResidentialCapacityCompatibility.Signature(query) != "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)" ||
                ResidentialCapacityCompatibility.Hash(query.GetMethodBody().GetILAsByteArray()) != "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892")
                throw new InvalidOperationException("Provider patch-metadata entry point differs.");
            string familyReason;
            if(activation!=null&&!activation.Validate(assemblies,out familyReason))throw new InvalidOperationException(familyReason);
            MethodBase[] providerBoundary;
            if(activation==null)providerBoundary=ProviderBoundaryMethods(provider);
            else
            {
                List<MethodBase> familyMethods=new List<MethodBase>();
                foreach(Assembly member in activation.Assemblies)familyMethods.AddRange(ResidentialCapacityProviderFamily.RequireMethods(member,ResidentialCapacityCanonicalIL.Material));
                providerBoundary=familyMethods.ToArray();
            }
            object sharedState = RequireActivatedSharedState(assemblies);
            FieldInfo lockerField = provider.GetType("HarmonyLib.PatchProcessor", true).GetField("locker", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (lockerField == null || !lockerField.IsAssembly || !lockerField.IsInitOnly || lockerField.FieldType != typeof(object))
                throw new InvalidOperationException("Provider synchronization field differs.");
            // The pinned eleven-byte cctor only allocates this object. It does
            // not initialize RuntimeDetour or a new shared-state registry.
            object processorLocker = lockerField.GetValue(null);
            if (processorLocker == null || ReferenceEquals(processorLocker, sharedState))
                throw new InvalidOperationException("Provider synchronization object is absent or aliased.");
            List<ResidentialCapacityPatchObservation> patches = new List<ResidentialCapacityPatchObservation>();
            // Exact fork order: PatchProcessor.locker -> shared state. Its
            // public query reacquires both Monitors reentrantly. Keep both for
            // the whole target batch; never acquire locker while holding state.
            ResidentialCapacityProviderLocks monitors;
            if(!ResidentialCapacityProviderLocks.TryAcquire(activation==null?new[]{processorLocker}:activation.Lockers,sharedState,out monitors,out familyReason))throw new InvalidOperationException(familyReason);
            using(monitors)
            {
                if(activation!=null&&!activation.Validate(AppDomain.CurrentDomain.GetAssemblies(),out familyReason))throw new InvalidOperationException(familyReason);
                foreach (MethodBase helper in providerBoundary)
                    if (((IDictionary)sharedState).Contains(helper))
                        throw new InvalidOperationException("Provider query/synchronization boundary has a patch-state entry: " + ResidentialCapacityCompatibility.Signature(helper));
                MethodInfo dictionaryReaderDefinition = provider.GetType("HarmonyLib.GeneralExtensions", true).GetMethod("GetValueSafe", BindingFlags.Public | BindingFlags.Static);
                MethodInfo[] readerDefinitions=activation==null?new[]{dictionaryReaderDefinition}:activation.Readers;
                foreach (object key in ((IDictionary)sharedState).Keys)
                foreach(MethodInfo readerDefinition in readerDefinitions)
                    if (IsDictionaryReaderFamily(key as MethodBase, readerDefinition))
                        throw new InvalidOperationException("Provider dictionary-reader generic family has a patch-state entry.");
                foreach (MethodBase target in ResidentialCapacityCompatibility.EffectiveTargets(assemblies, raw))
                {
                    bool registryEntry = ((IDictionary)sharedState).Contains(target);
                    object metadata = query.Invoke(null, new object[] { target });
                    if (metadata == null) { ValidateRegistryRead(registryEntry, true, 0); continue; }
                    if (metadata.GetType().Assembly != provider || metadata.GetType().FullName != "HarmonyLib.Patches") throw new InvalidOperationException("Unknown patch metadata shape.");
                    int firstPatch = patches.Count;
                    foreach (string kind in new string[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                    {
                        FieldInfo field = metadata.GetType().GetField(kind, BindingFlags.Public | BindingFlags.Instance);
                        IEnumerable entries = field == null ? null : field.GetValue(metadata) as IEnumerable;
                        if (entries == null) throw new InvalidOperationException("Unknown patch metadata collection: " + kind);
                        int count = 0;
                        foreach (object patch in entries)
                        {
                            if (++count > 256 || patches.Count >= 4096) throw new InvalidOperationException("Patch metadata exceeds diagnostic bounds.");
                            if (patch == null || patch.GetType().Assembly != provider || patch.GetType().FullName != "HarmonyLib.Patch") throw new InvalidOperationException("Unknown patch descriptor.");
                            FieldInfo ownerField = patch.GetType().GetField("owner", BindingFlags.Public | BindingFlags.Instance);
                            PropertyInfo hookProperty = patch.GetType().GetProperty("PatchMethod", BindingFlags.Public | BindingFlags.Instance);
                            string owner = ownerField == null ? null : ownerField.GetValue(patch) as string;
                            MethodInfo hook = hookProperty == null ? null : hookProperty.GetValue(patch, null) as MethodInfo;
                            if (string.IsNullOrEmpty(owner) || hook == null) throw new InvalidOperationException("Patch owner/hook is absent.");
                            FieldInfo priorityField=patch.GetType().GetField("priority",BindingFlags.Public|BindingFlags.Instance), beforeField=patch.GetType().GetField("before",BindingFlags.Public|BindingFlags.Instance), afterField=patch.GetType().GetField("after",BindingFlags.Public|BindingFlags.Instance);
                            if(priorityField==null||priorityField.FieldType!=typeof(int)||beforeField==null||afterField==null)throw new InvalidOperationException("Patch ordering metadata differs.");
                            string[] before=beforeField.GetValue(patch) as string[], after=afterField.GetValue(patch) as string[];
                            if(before==null||after==null||before.Length>256||after.Length>256)throw new InvalidOperationException("Patch ordering collections differ.");
                            if (hook is DynamicMethod || hook.DeclaringType == null || hook.Module is ModuleBuilder || hook.Module.Assembly is AssemblyBuilder || hook.IsGenericMethod || hook.ContainsGenericParameters)
                                throw new InvalidOperationException("Dynamic/generic patch hooks are not approved.");
                            MethodBody body = hook.GetMethodBody();
                            string canonical = hook.Module.Assembly.FullName == ResidentialCapacityOwnHooks.OwnAssemblyIdentity ? ResidentialCapacityCompatibility.Hash(Encoding.UTF8.GetBytes(raw?ResidentialCapacityCanonicalIL.RawMaterial(hook):ResidentialCapacityCanonicalIL.Material(hook))) : null;
                            patches.Add(new ResidentialCapacityPatchObservation(ResidentialCapacityCompatibility.Signature(target), kind, owner,
                                hook.Module.Assembly.FullName + " | " + ResidentialCapacityCompatibility.Signature(hook),
                                body == null ? null : ResidentialCapacityCompatibility.Hash(body.GetILAsByteArray()), canonical,(int)priorityField.GetValue(patch),before,after));
                        }
                    }
                    ValidateRegistryRead(registryEntry, false, patches.Count - firstPatch);
                }
                if (raw)
                {
                    bool shape=patches.Count==23;
                    foreach(ResidentialCapacityPatchObservation patch in patches)shape&=ResidentialCapacityRawHooks.Match(patch)>=0;
                    if(!shape)return ResidentialCapacityCompatibility.EvaluateRawPatches(original,patches.ToArray(),false);
                    MethodBase[] ownRawMethods=ResidentialCapacityRawHooks.RequireMethods(assemblies);
                    foreach(MethodBase helper in ownRawMethods)if(((IDictionary)sharedState).Contains(helper))
                        throw new InvalidOperationException("Approved RAW hook/helper itself has a patch-state entry: "+ResidentialCapacityOwnHooks.Key(helper));
                    ResidentialCapacityCompatibilityResult result=ResidentialCapacityCompatibility.EvaluateRawPatches(original,patches.ToArray(),true);
                    if(result.Kind!=ResidentialCapacityCompatibilityKind.Known)return result;
                    List<MethodBase> watched=new List<MethodBase>(ResidentialCapacityCompatibility.EffectiveTargets(assemblies,true));watched.AddRange(providerBoundary);watched.AddRange(ownRawMethods);
                    ResidentialCapacityRegistrySnapshot snapshot=new ResidentialCapacityRegistrySnapshot((IDictionary)sharedState,watched.ToArray(),readerDefinitions);
                    if(snapshot.Count!=46+providerBoundary.Length+ownRawMethods.Length)throw new InvalidOperationException("RAW certificate watch set is incomplete or duplicated.");
                    Type sharedType=null;List<Assembly> sources=new List<Assembly>();
                    foreach(Assembly assembly in assemblies)
                    {
                        string name=assembly.GetName().Name;
                        if(name=="Assembly-CSharp"||name=="ColossalManaged"||name=="UnityEngine"||ReferenceEquals(assembly,provider)||ResidentialCapacityRawHooks.IsSource(assembly))sources.Add(assembly);
                        Type candidate=assembly.GetType("CHSharedState",false);if(candidate!=null)sharedType=candidate;
                    }
                    FieldInfo stateField=sharedType.GetField("state",BindingFlags.Public|BindingFlags.Static), originalsField=sharedType.GetField("originals",BindingFlags.Public|BindingFlags.Static), versionField=sharedType.GetField("version",BindingFlags.Public|BindingFlags.Static);
                    certificate=new ResidentialCapacityPatchCertificate(result,sources.ToArray(),provider,sharedType,stateField,originalsField,versionField,lockerField,sharedState,originalsField.GetValue(null),processorLocker,snapshot,activation);
                    return result;
                }
                bool ownConfiguration = patches.Count == 5;
                foreach (ResidentialCapacityPatchObservation patch in patches) ownConfiguration &= ResidentialCapacityOwnHooks.Match(patch) >= 0;
                if (ownConfiguration)
                {
                    MethodBase[] ownMethods = ResidentialCapacityOwnHooks.RequireMethods(assemblies);
                    foreach (MethodBase helper in ownMethods)
                        if (((IDictionary)sharedState).Contains(helper))
                            throw new InvalidOperationException("Approved own hook/helper itself has a patch-state entry: " + ResidentialCapacityOwnHooks.Key(helper));
                    return ResidentialCapacityCompatibility.EvaluatePatches(original, patches.ToArray(), true);
                }
            }
            return ResidentialCapacityCompatibility.EvaluatePatches(original, patches.ToArray());
        }

        internal static void ValidateRegistryRead(bool registryEntry, bool metadataNull, int hookCount)
        {
            // With an empty allowlist, a present serialized entry may never be
            // certified by a null/empty public answer (including stale unpatch
            // entries). A query helper cannot hide hooks and produce Known.
            if (hookCount < 0 || (registryEntry && (metadataNull || hookCount == 0)) || (!registryEntry && (!metadataNull || hookCount != 0)))
                throw new InvalidOperationException("Provider registry/public metadata disagree or contain an uncertifiable empty patch-state entry.");
        }

        internal static bool IsDictionaryReaderFamily(MethodBase candidate, MethodInfo definition)
        {
            MethodInfo method = candidate as MethodInfo;
            return method != null && method.IsGenericMethod && method.GetGenericMethodDefinition().Equals(definition);
        }
        internal static MethodBase[] ProviderBoundaryMethods(Assembly provider)
        { return ProviderBoundaryMethods(provider, ResidentialCapacityCanonicalIL.Material); }
        internal static MethodBase[] ProviderBoundaryMethods(Assembly provider, Func<MethodBase, string> canonicalMaterial)
        {
            // Literal original-body pins from the release2.2.2-0 module. These
            // certify the concrete query/locking path, not arbitrary callees.
            string[,] pins = new string[,] {
                { "HarmonyLib.Harmony", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.Harmony::GetPatchInfo(System.Reflection.MethodBase)", "7", "fd18368224541cd12920bd0a47f954eb0a627b3463ee253d56d3b86af7348892", "289d55b90417ea058d8dab2d16c13b9216ed3acd130ee303208ab432f18b21a0" },
                { "HarmonyLib.PatchProcessor", "GetPatchInfo", "public static HarmonyLib.Patches HarmonyLib.PatchProcessor::GetPatchInfo(System.Reflection.MethodBase)", "63", "a7f9b75ee694a08b0715e56e55e203fba27821ff713cf5da71f45f6671f97812", "f2b67caa0b9521e323e46f1bebaa5585de8836f3235dcddb2dfe623104780ed4" },
                { "HarmonyLib.HarmonySharedState", "GetPatchInfo", "other static HarmonyLib.PatchInfo HarmonyLib.HarmonySharedState::GetPatchInfo(System.Reflection.MethodBase)", "45", "025f816e51cfa28290cccf131041fb9c5ff5a7d19065c3548b93793bdfb9326c", "0f017a35efb028f7d53949937a6126d72cf0d35abacb334d808f7244f026b765" },
                { "HarmonyLib.PatchProcessor", ".cctor", "private static System.Void HarmonyLib.PatchProcessor::.cctor()", "11", "c6560be813e7e4925e58305ea2c0e5d5fdb0d559f7ef4f9404cb3864bf188413", "6bec086d20f20b93b355c0bff7bc505ca9baa35c301e7905c2aef3bdc0892147" },
                { "HarmonyLib.PatchProcessor", "Patch", "public instance System.Reflection.MethodInfo HarmonyLib.PatchProcessor::Patch()", "289", "e2ab029607303cccacd2b3bad9f431029cc8981eb9ec153967e1e0b505c37404", "f74a0903a3fd8704200d62a442b80b1271dde1ab1b6dabc66f6be6146db67473" },
                { "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(HarmonyLib.HarmonyPatchType,System.String)", "128", "b0862c443f04da1d4ef056674adf5467e7fd3bc2f5baffd64f43a0d510a95ef9", "bcabd967d6382538248fcc104e9eb0fcb4b55558cbc021cd7e412d0b04172b24" },
                { "HarmonyLib.PatchProcessor", "Unpatch", "public instance HarmonyLib.PatchProcessor HarmonyLib.PatchProcessor::Unpatch(System.Reflection.MethodInfo)", "79", "f02a1010a2d264f7693c2eb5ef70eb12d5a155219b58bb3a81589c902fcdd578", "ccd5425fc05899e6b444a7dca91d44ad338d0209f4a1377a285c49f73584660b" },
                { "HarmonyLib.HarmonySharedState", "UpdatePatchInfo", "other static System.Void HarmonyLib.HarmonySharedState::UpdatePatchInfo(System.Reflection.MethodBase,System.Reflection.MethodInfo,HarmonyLib.PatchInfo)", "121", "b29c0516d18c7df4388189aeb3947bb6fdf281a2de28ad385eea051d3f1b200c", "2c09bd1271ac3f0523eb07264a9205953d391e3646f336f95a5e984e09dc8be7" },
                { "HarmonyLib.PatchInfoSerialization", "Deserialize", "other static HarmonyLib.PatchInfo HarmonyLib.PatchInfoSerialization::Deserialize(System.Byte[])", "38", "7260ba4e9c2e6632211a035b4a38133f075cfb45e894a3d86ef8959cf80cfc4c", "0d25604d3b0adad3e5bdb436d2655976b85424e93dc661c619f45bfb1cdc01f0" },
                { "HarmonyLib.PatchInfoSerialization", ".cctor", "private static System.Void HarmonyLib.PatchInfoSerialization::.cctor()", "22", "13aee74dab38f0c8f44f08bb9ac7270c0634ad5bd7741fbde4b9f61e54e9331e", "f48199e9974294a95bf9ba95b6efad5ab55f83d285a997d60e820d4adf7186a5" },
                { "HarmonyLib.PatchInfoSerialization+Binder", "BindToType", "public instance System.Type HarmonyLib.PatchInfoSerialization+Binder::BindToType(System.String,System.String)", "98", "a0d3d5e8efdd9c04a47b38d7a4dbede890c07f953a58580e5b13c2a6298f741d", "20ada12985ff5ee058368e01facc8e3ec413bf8a2bb5a2050c8782fd1480e3be" },
                { "HarmonyLib.PatchInfoSerialization+Binder", ".ctor", "public instance System.Void HarmonyLib.PatchInfoSerialization+Binder::.ctor()", "7", "79482b07da3cb366dabdeb668e0b64e6ae9ab931fc31cabc39b7e0973f292184", "a894cad35429bc88509bc23d83b7752d46685f3499ad6fe3b678f7e3c509e833" },
                { "HarmonyLib.Patches", ".ctor", "public instance System.Void HarmonyLib.Patches::.ctor(HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[],HarmonyLib.Patch[])", "121", "2e0e4f15bb1b1d793af5fc69e6a8de2a5a479773448c4ff6523a3b4fffe6d75b", "3750a79ae9cbd6948ad5bf5716ece9ac59359cd32cf73d27e089d1cb1d4c6935" },
                { "HarmonyLib.Patch", "get_PatchMethod", "public instance System.Reflection.MethodInfo HarmonyLib.Patch::get_PatchMethod()", "138", "e217b44aad0154e073da46658f259b9b7da798d9c000f308050e2116d6554fbc", "eaf1f3feb184af5f60ad3e71d2944ca2b448b3a8da488e2f2bf448846ad76626" },
                { "HarmonyLib.Patch", "<get_PatchMethod>b__10_2", "private instance System.Boolean HarmonyLib.Patch::<get_PatchMethod>b__10_2(System.Reflection.Module)", "32", "b0c927f633a1fd6277744eb6c169fd8fe76011126f97067560d3470d9b8849c6", "a41236a9613af467c13fe3da4f81e2543f4532213e34fdae4de84fbba1571661" },
                { "HarmonyLib.Patch+<>c", ".cctor", "private static System.Void HarmonyLib.Patch+<>c::.cctor()", "11", "3f4efe4cade4b6fd7d96d38c734cee85034d967ca7d445f2ef190c780ca1a131", "3eb70ef5ce8e46e2943a21effa26948ba6e0ec15af2f3985e07017d85da43c2a" },
                { "HarmonyLib.Patch+<>c", ".ctor", "public instance System.Void HarmonyLib.Patch+<>c::.ctor()", "7", "5404b67706c80f0fcdcec966424c5cb02ef3334cefc0212fcc50697516197539", "d9099f4ec66713c09375e9b55405a1e5684999fce003cfe4f40a6a8cf2d6aa46" },
                { "HarmonyLib.Patch+<>c", "<get_PatchMethod>b__10_0", "other instance System.Boolean HarmonyLib.Patch+<>c::<get_PatchMethod>b__10_0(System.Reflection.Assembly)", "20", "cf1c59e1f2cb5777320155564d1ba6884957e3c6360a436a1369d4da658075e3", "07d3cfac06b2f9a49cac61cd138f757cd866c442319fdffe0ef9c4210d13f0da" },
                { "HarmonyLib.Patch+<>c", "<get_PatchMethod>b__10_1", "other instance System.Collections.Generic.IEnumerable`1[[System.Reflection.Module, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]] HarmonyLib.Patch+<>c::<get_PatchMethod>b__10_1(System.Reflection.Assembly)", "7", "6597dd9db35dce4c6c1db8b201b8f2b1b6db2b69eddb4a505751fe8edbf71f94", "11326bb9d66a7f7a3eedbd0f5744b8399174ae1ba89ca575bc46eb7ac0676f3f" },
                { "HarmonyLib.GeneralExtensions", "GetValueSafe", "public static  HarmonyLib.GeneralExtensions::GetValueSafe(,)", "23", "7addb97526a38270460c03247cefe9a307305887ddd890a05ca112df288495d7", "1c83beb7b8a669080cd3177797ac7df903f7e93ecc2d2bd66990a47f519be3d5" },
                { "HarmonyLib.PatchInfoSerialization", "Serialize", "other static System.Byte[] HarmonyLib.PatchInfoSerialization::Serialize(HarmonyLib.PatchInfo)", "39", "c5a76a666720de778dc216faac3cb8f1f2343a880ddb9db1a5dfc5ec01cc6f72", "b29f7144b737865f0fcb3631d775ded801f8f4fa841b0766b4c12c720df34419" }
            };
            List<MethodBase> result = new List<MethodBase>();
            for (int index = 0; index < pins.GetLength(0); index++)
            {
                Type type = provider.GetType(pins[index, 0], true); MethodBase selected = null;
                if (pins[index, 1] == ".cctor") selected = type.TypeInitializer;
                else if (pins[index, 1] == ".ctor") foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    { if (ResidentialCapacityCompatibility.Signature(constructor) == pins[index, 2]) selected = constructor; }
                else foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (ResidentialCapacityCompatibility.Signature(method) == pins[index, 2] || (pins[index, 0] == "HarmonyLib.GeneralExtensions" && method.Name == "GetValueSafe")) selected = method;
                ResidentialCapacityCompatibility.MethodPin pin = new ResidentialCapacityCompatibility.MethodPin("CitiesHarmony.Harmony", pins[index, 0], pins[index, 1], pins[index, 2], pins[index, 4], pins[index, 5], int.Parse(pins[index, 3], CultureInfo.InvariantCulture));
                if (selected == null) throw new InvalidOperationException("Provider query/synchronization method differs: " + pins[index, 2]);
                // Generic parameter FullName differs on Mono vs the framework
                // metadata reader. For this one generic definition retain the
                // exact raw IL pin and use a literal normalized body pin instead
                // of interpreting that representation difference as a change.
                if (pins[index, 0] == "HarmonyLib.GeneralExtensions")
                {
                    byte[] il = selected.GetMethodBody().GetILAsByteArray();
                    if (il.Length != pin.Length || ResidentialCapacityCompatibility.Hash(il) != pin.Il || ResidentialCapacityCompatibility.Hash(Encoding.UTF8.GetBytes(canonicalMaterial(selected))) != "5451ce88f0f3315a4100d16a7f90df4ab4cc840cf74b3dec3d7f5ba5368431f9")
                        throw new InvalidOperationException("Provider dictionary-reader raw/canonical IL differs.");
                }
                else if (!ResidentialCapacityCompatibility.ObserveMethod(selected, pin).Known)
                    throw new InvalidOperationException("Provider query/synchronization original body differs: " + pins[index, 2]);
                result.Add(selected);
            }
            MethodInfo dictionaryRead = provider.GetType("HarmonyLib.GeneralExtensions", true).GetMethod("GetValueSafe", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (dictionaryRead == null || !dictionaryRead.IsGenericMethodDefinition || dictionaryRead.GetGenericArguments().Length != 2)
                throw new InvalidOperationException("Provider concrete generic dictionary reader differs.");
            Type[] genericArguments = dictionaryRead.GetGenericArguments(); ParameterInfo[] readerParameters = dictionaryRead.GetParameters();
            if (readerParameters.Length != 2 || dictionaryRead.ReturnType != genericArguments[1] || readerParameters[1].ParameterType != genericArguments[0] || !readerParameters[0].ParameterType.IsGenericType || readerParameters[0].ParameterType.GetGenericTypeDefinition().FullName != "System.Collections.Generic.Dictionary`2" ||
                readerParameters[0].ParameterType.GetGenericArguments()[0] != genericArguments[0] || readerParameters[0].ParameterType.GetGenericArguments()[1] != genericArguments[1])
                throw new InvalidOperationException("Provider concrete dictionary reader signature differs.");
            foreach (Type argument in genericArguments) if (argument.GenericParameterAttributes != GenericParameterAttributes.None || argument.GetGenericParameterConstraints().Length != 0)
                throw new InvalidOperationException("Provider dictionary reader constraints differ.");
            // Pin the definition body and reject BOTH registry keys: definition
            // and the exact closed reader called by SharedState. Use types from
            // the provider reflection universe (also works metadata-only).
            Type methodBaseType = provider.GetType("HarmonyLib.Harmony", true).GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static).GetParameters()[0].ParameterType;
            Type bytesType = provider.GetType("HarmonyLib.PatchInfoSerialization", true).GetMethod("Deserialize", BindingFlags.NonPublic | BindingFlags.Static).GetParameters()[0].ParameterType;
            result.Add(dictionaryRead.MakeGenericMethod(methodBaseType, bytesType));
            return result.ToArray();
        }

        private static object RequireActivatedSharedState(Assembly[] assemblies)
        {
            Type shared = null;
            foreach (Assembly assembly in assemblies)
            {
                Type candidate = assembly.GetType("CHSharedState", false);
                if (candidate == null) continue;
                if (shared != null && !ReferenceEquals(shared, candidate)) throw new InvalidOperationException("Multiple central provider state types.");
                shared = candidate;
            }
            // This type is generated by the pinned provider without a cctor.
            // Checking that fact before GetValue prevents the observer from
            // starting a provider initializer just to ask for patch metadata.
            if (shared == null || shared.TypeInitializer != null || !shared.IsAbstract || !shared.IsSealed)
                throw new InvalidOperationException("Central provider has not published the verified initialized metadata state.");
            FieldInfo version = shared.GetField("version", BindingFlags.Public | BindingFlags.Static);
            FieldInfo stateField = shared.GetField("state", BindingFlags.Public | BindingFlags.Static);
            FieldInfo originalsField = shared.GetField("originals", BindingFlags.Public | BindingFlags.Static);
            if (version == null || version.FieldType != typeof(int) || stateField == null || stateField.FieldType != typeof(Dictionary<MethodBase, byte[]>) ||
                originalsField == null || originalsField.FieldType != typeof(Dictionary<MethodInfo, MethodBase>))
                throw new InvalidOperationException("Central provider shared-state field layout differs.");
            object state = stateField.GetValue(null); IDictionary originals = originalsField.GetValue(null) as IDictionary;
            if (!object.Equals(version.GetValue(null), 102) || state == null || originals == null)
                throw new InvalidOperationException("Central provider metadata initialization is incomplete.");
            if(!System.Threading.Monitor.TryEnter(state))throw new InvalidOperationException("The central provider state is busy.");
            try{if(((IDictionary)state).Count==0||originals.Count==0)throw new InvalidOperationException("No activated provider patch metadata exists; caller must activate the provider before requesting certification.");}
            finally{System.Threading.Monitor.Exit(state);}
            return state;
        }
    }
}
