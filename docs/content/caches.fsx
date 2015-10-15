(*** hide ***)
#I "../../bin/v4.5/"
(**
Compiler Services: Notes on the FSharpChecker caches
=================================================

This is a design note on the FSharpChecker component and its caches.  See also the notes on the [FSharpChecker operations queue](queue.html)

Each FSharpChecker object maintains a set of caches.  These are

* ``scriptClosureCache`` - an MRU cache of default size ``projectCacheSize`` that caches the 
  computation of GetProjectOptionsFromScript. This computation can be lengthy as it can involve processing the transative closure
  of all ``#load`` directives, which in turn can mean parsing an unbounded number of script files

* ``incrementalBuildersCache`` - an MRU cache of projects where a handle is being kept to their incremental checking state, 
  of default size ``projectCacheSize`` (= 3 unless explicitly set as a parameter).  
  The "current background project" (see the [FSharpChecker operations queue](queue.html)) 
  will be one of these projects.  When analyzing large collections of projects, this cache usually occupies by far the most memory.
  Increasing the size of this cache can dramatically decrease incremental computation of project-wide checking, or of checking
  individual files within a project, but can very greatly increase memory usage.

* ``braceMatchCache`` - an MRU cache of size ``braceMatchCacheSize`` (default = 5) keeping the results of calls to MatchBraces, keyed by filename, source and project options.

* ``parseFileInProjectCache`` - an MRU cache of size ``parseFileInProjectCacheSize`` (default = 2) keeping the results of ParseFileInProject, 
  keyed by filename, source and project options.

* ``parseAndCheckFileInProjectCache`` - an MRU cache of size ``incrementalTypeCheckCacheSize`` (default = 5) keeping the results of 
  ParseAndCheckFileInProject, CheckFileInProject and/or CheckFileInProjectIfReady. This is keyed by filename, file source 
  and project options.  The results held in this cache are only returned if they would reflect an accurate parse and check of the
  file.

* ``parseAndCheckFileInProjectCachePossiblyStale`` - a somewhat peculiar MRU cache of size ``incrementalTypeCheckCacheSize`` (default = 5) 
  keeping the results of ParseAndCheckFileInProject, CheckFileInProject and CheckFileInProjectIfReady, 
  keyed by filename and project options. This cache is accessed by TryGetRecentTypeCheckResultsForFile. Because the results
  are accessed regardless of the content of the file, the checking results returned may be "stale".

* ``getToolTipTextCache`` - an aged lookup cache of strong size ``getToolTipTextSize`` (default = 5) computing the results of GetToolTipText.

* ``ilModuleReaderCache`` - an aged lookup of weak references to "readers" for references .NET binaries. Because these
  are all weak references, you can generally ignore this cache, since its entries will be automatically collected.
  Strong references to binary readers will be kept by other FCS data structures, e.g. any project checkers, symbols or project checking results.

  In more detail, the bytes for referenced .NET binaries are read into memory all at once, eagerly. Files are not left 
  open or memory-mapped when using FSharpChecker (as opposed to FsiEvaluationSession, which loads assemblies using reflection). 
  The purpose of this cache is mainly to ensure that while setting up compilation, the reads of mscorlib, FSharp.Core and so on
  amortize cracking the DLLs.

* ``frameworkTcImportsCache`` - an aged lookup of strong size 8 which caches the process of setting up type checking against a set of system
  components (e.g. a particular version of mscorlib, FSharp.Core and other system DLLs).  These resources are automatically shared between multiple
  project checkers which happen to reference the same set of system assemblies.

Profiling the memory used by the various caches can be done by looking for the corresponding static roots in memory profiling traces.

The sizes of some of these caches can be adjusted by giving parameters to FSharpChecker.  Unless otherwise noted, 
the cache sizes above indicate the "strong" size of the cache, where memory is held regardless of the memory 
pressure on the system. Some of the caches can also hold "weak" references which can be collected at will by the GC.

> Note: Because of these caches, uou should generally use one global, shared FSharpChecker for everything in an IDE application.


Low-Memory Condition
-------

Version 1.4.0.8 added a "maximum memory" limit specified by the `MaxMemory` property on FSharpChecker (in MB). If an FCS project operation
is performed (see `CheckMaxMemoryReached` in `service.fs`) and `System.GC.GetTotalMemory(false)` reports a figure greater than this, then
the strong sizes of all FCS caches are reduced to either 0 or 1.  This happens for the remainder of the lifetime of the FSharpChecker object. 
In practice this will still make tools like the Visual Studio F# Power Tools usable, but some operations like renaming across multiple 
projects may take substantially longer.

For a 32-bit process the default threshold is 1.7GB of allocated managed memory in a process, see `maxMBDefault` in `service.fs`. For a 64-bit process 
it is twice this.

Reducing the FCS strong cache sizes does not guarantee there will be enough memory to continue operations - even holding one project 
strongly may exceed a process memory budget. It just means FCS may hold less memory strongly.

If you do not want the maximum memory limit to apply then set MaxMemory to System.Int32.MaxValue.

Summary
-------

In this design note, you learned that the FSharpChecker component keeps a set of caches in order to support common
incremental analysis scenarios reasonably efficiently. They correspond roughly to the original caches and sizes 
used by the Visual F# Tools, from which the FSharpChecker component derives.

In long running, highly interactive, multi-project scenarios you should carefully 
consider the cache sizes you are using and the tradeoffs involved between incremental multi-project checking and memory usage.
*)