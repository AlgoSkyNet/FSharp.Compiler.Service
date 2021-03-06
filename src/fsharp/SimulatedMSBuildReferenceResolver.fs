// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#if INTERACTIVE
#load "../utils/ResizeArray.fs" "../absil/illib.fs" "../fsharp/ReferenceResolver.fs"
#else
module internal Microsoft.FSharp.Compiler.SimulatedMSBuildReferenceResolver 
#endif

open System
open System.IO
open System.Reflection
open Microsoft.Win32
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.ReferenceResolver
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library

let internal SimulatedMSBuildResolver =
    { new Resolver with 
        member __.HighestInstalledNetFrameworkVersion() = "v4.5"
        member __.DotNetFrameworkReferenceAssembliesRootDirectory = 
#if RESHAPED_MSBUILD
            ""
#else
            if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then 
                let PF = 
                    match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
                    | null -> Environment.GetEnvironmentVariable("ProgramFiles")  // if PFx86 is null, then we are 32-bit and just get PF
                    | s -> s 
                PF + @"\Reference Assemblies\Microsoft\Framework\.NETFramework"
            else
                ""
#endif

        member __.Resolve(resolutionEnvironment, references, targetFrameworkVersion, targetFrameworkDirectories, targetProcessorArchitecture,                
                            fsharpCoreDir, explicitIncludeDirs, implicitIncludeDir, logMessage, logWarningOrError) =

#if !RESHAPED_MSBUILD
            let registrySearchPaths() = 
              [ let registryKey = @"Software\Microsoft\.NetFramework";
                use key = Registry.LocalMachine.OpenSubKey(registryKey)
                match key with 
                | null -> ()
                | _ -> 
                for subKeyName in key.GetSubKeyNames() do
                    use subKey = key.OpenSubKey(subKeyName)
                    use subSubKey = subKey.OpenSubKey("AssemblyFoldersEx")
                    match subSubKey with 
                    | null -> ()
                    | _ -> 
                        for subSubSubKeyName in subSubKey.GetSubKeyNames() do
                            use subSubSubKey = subSubKey.OpenSubKey(subSubSubKeyName)
                            match subSubSubKey.GetValue(null) with 
                            | :? string as s -> yield s
                            | _ -> () 
                use subSubKey = key.OpenSubKey("AssemblyFolders")
                match subSubKey with 
                | null -> ()
                | _ -> 
                    for subSubSubKeyName in subSubKey.GetSubKeyNames() do
                        let subSubSubKey = subSubKey.OpenSubKey(subSubSubKeyName)
                        match subSubSubKey.GetValue(null) with 
                        | :? string as s -> yield s
                        | _ -> ()  ]
#endif

            let results = ResizeArray()
            let searchPaths = 
              [ yield! targetFrameworkDirectories 
                yield! explicitIncludeDirs 
                yield fsharpCoreDir
                yield implicitIncludeDir 
#if !RESHAPED_MSBUILD
                if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then 
                    yield! registrySearchPaths() 
#endif
              ]

            for (r, baggage) in references do
                //printfn "resolving %s" r
                let mutable found = false
                let success path = 
                    if not found then 
                        //printfn "resolved %s --> %s" r path
                        found <- true
                        results.Add { itemSpec = path; prepareToolTip = snd; baggage=baggage } 

                try 
                    if not found && Path.IsPathRooted(r) then 
                        if FileSystem.SafeExists(r) then 
                            success r
                with e -> logWarningOrError false "SR001" (e.ToString())

#if !RESHAPED_MSBUILD
                // For this one we need to get the version search exactly right, without doing a load
                try 
                    if not found && r.StartsWith("FSharp.Core, Version=")  && Environment.OSVersion.Platform = PlatformID.Win32NT then 
                        let n = AssemblyName(r)
                        let fscoreDir0 = 
                            let PF = 
                                match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
                                | null -> Environment.GetEnvironmentVariable("ProgramFiles")  
                                | s -> s 
                            PF + @"\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\"  + n.Version.ToString()
                        let trialPath = Path.Combine(fscoreDir0,n.Name + ".dll")
                        if FileSystem.SafeExists(trialPath) then 
                            success trialPath
                with e -> logWarningOrError false "SR001" (e.ToString())
#endif

                let isFileName = 
                    r.EndsWith("dll",StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith("exe",StringComparison.OrdinalIgnoreCase)  

                let qual = if isFileName then r else try AssemblyName(r).Name + ".dll"  with _ -> r + ".dll"

                for searchPath in searchPaths do 
                  try 
                    if not found then 
                        let trialPath = Path.Combine(searchPath,qual)
                        if FileSystem.SafeExists(trialPath) then 
                            success trialPath
                  with e -> logWarningOrError false "SR001" (e.ToString())

#if !RESHAPED_MSBUILD
                try 
                    // Seach the GAC on Windows
                    if not found && not isFileName && Environment.OSVersion.Platform = PlatformID.Win32NT then 
                        let n = AssemblyName(r)
                        let netfx = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                        let gac = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(netfx.TrimEnd('\\'))),"assembly")
                        match n.Version, n.GetPublicKeyToken()  with 
                        | null, _ | _,null -> 
                            let options = 
                                [ for gacdir in Directory.EnumerateDirectories(gac) do 
                                    let assdir = Path.Combine(gacdir,n.Name)
                                    if Directory.Exists(assdir) then 
                                        for tdir in Directory.EnumerateDirectories(assdir) do 
                                            let trialPath = Path.Combine(tdir,qual)
                                            if FileSystem.SafeExists(trialPath) then 
                                                yield trialPath ]
                            //printfn "sorting GAC paths: %A" options
                            options 
                            |> List.sort // puts latest version last
                            |> List.tryLast
                            |> function None -> () | Some p -> success p

                        | v,tok -> 
                            for gacdir in Directory.EnumerateDirectories(gac) do 
                                //printfn "searching GAC directory: %s" gacdir
                                let assdir = Path.Combine(gacdir,n.Name)
                                if Directory.Exists(assdir) then 
                                    //printfn "searching GAC directory: %s" assdir

                                    let tokText = String.concat "" [| for b in tok -> sprintf "%02x" b |]
                                    let verdir = Path.Combine(assdir,"v4.0_"+v.ToString()+"__"+tokText)
                                    //printfn "searching GAC directory: %s" verdir

                                    if Directory.Exists(verdir) then 
                                        let trialPath = Path.Combine(verdir,qual)
                                        //printfn "searching GAC: %s" trialPath
                                        if FileSystem.SafeExists(trialPath) then 
                                            success trialPath
                with e -> logWarningOrError false "SR001" (e.ToString())
#endif

            results.ToArray() }

let internal GetBestAvailableResolver(msbuildEnabled: bool) = 
#if RESHAPED_MSBUILD
    ignore msbuildEnabled
#else
    let tryMSBuild v = 
        // Detect if MSBuild is on the machine, if so use the resolver from there
        let mb = try Assembly.Load(sprintf "Microsoft.Build.Framework, Version=%s.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" v) |> Option.ofObj with _ -> None
        let ass = mb |> Option.bind (fun _ -> try Assembly.Load(sprintf "FSharp.Compiler.Service.MSBuild.v%s" v) |> Option.ofObj with _ -> None)
        let ty = ass |> Option.bind (fun ass -> ass.GetType("Microsoft.FSharp.Compiler.MSBuildReferenceResolver") |> Option.ofObj)
        let obj = ty |> Option.bind (fun ty -> ty.InvokeMember("get_Resolver",BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.InvokeMethod ||| BindingFlags.NonPublic, null, null, [| |]) |> Option.ofObj)
        let resolver = obj |> Option.bind (fun obj -> match obj with :? Resolver as r -> Some r | _ -> None)
        resolver
    match (if msbuildEnabled then tryMSBuild "12" else None) with 
    | Some r -> r
    | None -> 
#endif
    SimulatedMSBuildResolver 


#if INTERACTIVE
// Some manual testing
SimulatedMSBuildResolver.DotNetFrameworkReferenceAssembliesRootDirectory
SimulatedMSBuildResolver.HighestInstalledNetFrameworkVersion()

let fscoreDir = 
    if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then // file references only valid on Windows 
        let PF = 
            match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
            | null -> Environment.GetEnvironmentVariable("ProgramFiles")  // if PFx86 is null, then we are 32-bit and just get PF
            | s -> s 
        PF + @"\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\4.4.0.0"  
    else 
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()

let resolve s = 
    SimulatedMSBuildResolver.Resolve(ResolutionEnvironment.CompileTimeLike,[| for a in s -> (a, "") |],"v4.5.1", [SimulatedMSBuildResolver.DotNetFrameworkReferenceAssembliesRootDirectory + @"\v4.5.1" ],"", "", fscoreDir,[],__SOURCE_DIRECTORY__,ignore, (fun _ _ -> ()), (fun _ _-> ()))

// Resolve partial name to something on search path
resolve ["FSharp.Core" ]

// Resolve DLL name to something on search path
resolve ["FSharp.Core.dll" ]

// Resolve from reference assemblies
resolve ["System"; "mscorlib"; "mscorlib.dll" ]

// Resolve from Registry AssemblyFolders
resolve ["Microsoft.SqlServer.Dmf.dll"; "Microsoft.SqlServer.Dmf"  ]

// Resolve exact version of FSharp.Core
resolve [ "FSharp.Core, Version=4.4.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" ]

// Resolve from GAC:
resolve [                 "EventViewer, Version=6.3.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" ]

// Resolve from GAC:
resolve [                 "EventViewer" ]

resolve [                 "Microsoft.SharePoint.Client, Version=15.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c" ]
resolve [                 "Microsoft.SharePoint.Client, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c" ]
#endif

