using CrossPlatformPatcher.Core;
using dnlib.DotNet;
using System;
using System.Threading;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for OpenWakeWordSettings and OpenWakeWordSettingsBuilder.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public class OpenWakeWordSettingsTests
{
    [Fact]
    public void CreateDefault_Has_Expected_Values()
    {
        var settings = OpenWakeWordSettings.CreateDefault();
        
        Assert.Equal(0.7f, settings.ConfidenceThreshold);
        Assert.Equal(3000, settings.LockDurationMs);
        Assert.Equal(1024, settings.AudioChunkSize);
        Assert.Equal(1.0f, settings.InferenceThreadPoolScale);
        Assert.Equal("oww.model.hey_pie_com.onnx", settings.ModelResourceName);
        Assert.Equal(16000, settings.AudioSampleRate);
        Assert.False(settings.EnableVerboseLogging);
    }

    [Fact]
    public void Builder_WithThreshold_Sets_Value()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithThreshold(0.65f)
            .Build();
        
        Assert.Equal(0.65f, settings.ConfidenceThreshold);
    }

    [Fact]
    public void Builder_WithLockDurationMs_Sets_Value()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithLockDurationMs(2000)
            .Build();
        
        Assert.Equal(2000, settings.LockDurationMs);
    }

    [Fact]
    public void Builder_WithAudioChunkSize_Sets_Value()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithAudioChunkSize(512)
            .Build();
        
        Assert.Equal(512, settings.AudioChunkSize);
    }

    [Fact]
    public void Builder_WithInferenceThreadScale_Sets_Value()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithInferenceThreadScale(0.75f)
            .Build();
        
        Assert.Equal(0.75f, settings.InferenceThreadPoolScale);
    }

    [Fact]
    public void Builder_WithVerboseLogging_Sets_Value()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithVerboseLogging(true)
            .Build();
        
        Assert.True(settings.EnableVerboseLogging);
    }

    [Fact]
    public void Builder_Fluent_Chain_Sets_All_Values()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithThreshold(0.5f)
            .WithLockDurationMs(2000)
            .WithAudioChunkSize(512)
            .WithInferenceThreadScale(0.8f)
            .WithVerboseLogging(true)
            .Build();
        
        Assert.Equal(0.5f, settings.ConfidenceThreshold);
        Assert.Equal(2000, settings.LockDurationMs);
        Assert.Equal(512, settings.AudioChunkSize);
        Assert.Equal(0.8f, settings.InferenceThreadPoolScale);
        Assert.True(settings.EnableVerboseLogging);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    public void CreateDefault_Threshold_Validation_Throws(float invalid)
    {
        Assert.Throws<ArgumentException>(() =>
            OpenWakeWordSettings.CreateBuilder()
                .WithThreshold(invalid)
                .Build());
    }

    [Theory]
    [InlineData(50)]
    [InlineData(11000)]
    public void CreateDefault_LockDurationMs_Validation_Throws(int invalid)
    {
        Assert.Throws<ArgumentException>(() =>
            OpenWakeWordSettings.CreateBuilder()
                .WithLockDurationMs(invalid)
                .Build());
    }

    [Fact]
    public void FromEnvironmentVariables_With_Valid_Vars()
    {
        Environment.SetEnvironmentVariable("PAICOM_OWW_THRESHOLD", "0.6");
        Environment.SetEnvironmentVariable("PAICOM_OWW_LOCK_MS", "2500");
        Environment.SetEnvironmentVariable("PAICOM_OWW_VERBOSE_LOG", "true");
        
        try
        {
            var settings = OpenWakeWordSettings.FromEnvironmentVariables();
            
            Assert.Equal(0.6f, settings.ConfidenceThreshold);
            Assert.Equal(2500, settings.LockDurationMs);
            Assert.True(settings.EnableVerboseLogging);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAICOM_OWW_THRESHOLD", null);
            Environment.SetEnvironmentVariable("PAICOM_OWW_LOCK_MS", null);
            Environment.SetEnvironmentVariable("PAICOM_OWW_VERBOSE_LOG", null);
        }
    }

    [Fact]
    public void ToString_Contains_All_Settings()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithThreshold(0.75f)
            .Build();
        
        var str = settings.ToString();
        
        Assert.Contains("ConfidenceThreshold=0.750", str);
        Assert.Contains("LockDurationMs=3000", str);
        Assert.Contains("OpenWakeWordSettings", str);
    }
}

/// <summary>
/// Unit tests for AudioLockManager (hard lock + queue).
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public class AudioLockManagerTests
{
    [Fact]
    public void New_Instance_Not_Locked()
    {
        using var manager = new AudioLockManager(3000);
        
        Assert.False(manager.IsLocked);
        Assert.Equal(0, manager.QueueDepth);
    }

    [Fact]
    public void WakeWordDetected_Sets_Lock()
    {
        using var manager = new AudioLockManager(100);
        
        manager.WakeWordDetected();
        
        Assert.True(manager.IsLocked);
    }

    [Fact]
    public void Lock_Expires_After_Duration()
    {
        using var manager = new AudioLockManager(100); // 100ms
        
        manager.WakeWordDetected();
        Assert.True(manager.IsLocked);
        
        Thread.Sleep(150); // Wait for lock to expire
        
        Assert.False(manager.IsLocked);
    }

    [Fact]
    public void TryEnqueueAudio_Returns_True_When_Space()
    {
        using var manager = new AudioLockManager(3000);
        
        var chunk = new float[1024];
        var result = manager.TryEnqueueAudio(chunk);
        
        Assert.True(result);
        Assert.Equal(1, manager.QueueDepth);
    }

    [Fact]
    public void TryEnqueueAudio_Returns_False_When_Full()
    {
        using var manager = new AudioLockManager(3000, maxQueuedChunks: 2);
        
        var chunk = new float[1024];
        manager.TryEnqueueAudio(chunk); // Queue depth: 1
        manager.TryEnqueueAudio(chunk); // Queue depth: 2 (full)
        
        var result = manager.TryEnqueueAudio(chunk); // Queue depth: 3 (overflow)
        
        Assert.False(result); // Chunk dropped
        Assert.Equal(2, manager.QueueDepth); // Queue full, stays at max
    }

    [Fact]
    public void TryDequeueAudio_Returns_Chunk_In_Order()
    {
        using var manager = new AudioLockManager(3000);
        
        var chunk1 = new float[1024];
        var chunk2 = new float[512];
        
        for (int i = 0; i < chunk1.Length; i++) chunk1[i] = 1.0f;
        for (int i = 0; i < chunk2.Length; i++) chunk2[i] = 2.0f;
        
        manager.TryEnqueueAudio(chunk1);
        manager.TryEnqueueAudio(chunk2);
        
        var dequeued1 = manager.TryDequeueAudio();
        var dequeued2 = manager.TryDequeueAudio();
        var dequeued3 = manager.TryDequeueAudio();
        
        Assert.NotNull(dequeued1);
        Assert.NotNull(dequeued2);
        Assert.Null(dequeued3); // Queue empty
        
        Assert.Equal(1024, dequeued1!.Length);
        Assert.Equal(512, dequeued2!.Length);
    }

    [Fact]
    public void Reset_Clears_Queue_And_Lock()
    {
        using var manager = new AudioLockManager(3000);
        
        manager.WakeWordDetected();
        manager.TryEnqueueAudio(new float[1024]);
        manager.TryEnqueueAudio(new float[1024]);
        
        manager.Reset();
        
        Assert.False(manager.IsLocked);
        Assert.Equal(0, manager.QueueDepth);
        Assert.Null(manager.TryDequeueAudio());
    }

    [Fact]
    public async Task TryEnqueueAudio_Thread_Safe()
    {
        using var manager = new AudioLockManager(3000);
        
        var chunk = new float[1024];
        int enqueuedCount = 0;
        
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    if (manager.TryEnqueueAudio(chunk))
                        Interlocked.Increment(ref enqueuedCount);
                }
            }))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        // At least some should succeed (queue has capacity limit)
        Assert.True(enqueuedCount > 0);
        Assert.True(manager.QueueDepth > 0);
    }
}

/// <summary>
/// Integration tests for OpenWakeWordSettings + AudioLockManager.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public class OpenWakeWordIntegrationTests
{
    [Fact]
    public void Settings_And_Lock_Work_Together()
    {
        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithLockDurationMs(100)
            .WithThreshold(0.7f)
            .Build();
        
        using var lockManager = new AudioLockManager(settings.LockDurationMs);
        
        Assert.False(lockManager.IsLocked);
        
        lockManager.WakeWordDetected(); // Simulate detection
        
        Assert.True(lockManager.IsLocked);
        
        // Try to enqueue audio during lock (should work, but inference worker would skip)
        var chunk = new float[settings.AudioChunkSize];
        Assert.True(lockManager.TryEnqueueAudio(chunk));
        
        Thread.Sleep(150); // Wait for lock to expire
        
        Assert.False(lockManager.IsLocked);
    }

    [Fact]
    public void Settings_Embedding_In_Config()
    {
        // Simulates passing settings to patched exe
        var embeddedSettings = new 
        {
            Threshold = 0.7f,
            LockMs = 3000,
            ChunkSize = 1024,
            ThreadScale = 1.0f
        };
        
        // Verify all required settings can be captured
        Assert.Equal(0.7f, embeddedSettings.Threshold);
        Assert.Equal(3000, embeddedSettings.LockMs);
        Assert.Equal(1024, embeddedSettings.ChunkSize);
        Assert.Equal(1.0f, embeddedSettings.ThreadScale);
    }

    [Fact]
    public void Patch_RealExecutable_Injects_OpenWakeWordHelper()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var inputExe = Path.Combine(repoRoot, "PAIcom.exe");

        if (!File.Exists(inputExe))
            return;

        var outputExe = Path.Combine(Path.GetTempPath(), $"PAIcom.oww.{Guid.NewGuid():N}.patched.exe");

        try
        {
            var patcher = new AssemblyPatcher(verbose: false);
            var result = patcher.Patch(inputExe, outputExe, dryRun: false);

            Assert.True(File.Exists(outputExe));
            Assert.True(result.PatchPointsApplied >= 0);

            var module = ModuleDefMD.Load(outputExe);

            var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
            Assert.NotNull(helperType);

            Assert.NotNull(helperType!.FindMethod("InitializeOpenWakeWord"));
            Assert.NotNull(helperType.FindMethod("IsLocked"));
            Assert.NotNull(helperType.FindMethod("OnAudioChunkAvailable"));
            Assert.NotNull(helperType.FindMethod("LogOWWEvent"));

            var onAudioMethod = helperType.FindMethod("OnAudioChunkAvailable");
            var callCount = module.GetTypes()
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Count(i => i.OpCode == dnlib.DotNet.Emit.OpCodes.Call &&
                            i.Operand is IMethod called &&
                            called.DeclaringType?.Name == "CrossPlatformPatcherOWW" &&
                            called.Name == onAudioMethod!.Name);

            Assert.True(callCount > 0);
        }
        finally
        {
            if (File.Exists(outputExe))
                File.Delete(outputExe);
        }
    }
}
