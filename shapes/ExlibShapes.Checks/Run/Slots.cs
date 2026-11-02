using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExpandedLib.Shapes.Checks;

/// <summary>Machine-wide run slots: advisory-locked files shared with <c>vsshape gate</c>, so old and new gates split
/// one pool. A held slot is released by disposing it or ending the process.</summary>
public sealed class Slots {
  private const int LockEx = 2;
  private const int LockNb = 4;
  private const int LockUn = 8;
  private const int OpenReadWriteCreate = 0x42;

  private readonly string dir;

  /// <summary>The slots under <paramref name="dir"/>, <paramref name="count"/> of them.</summary>
  public Slots(string dir, int count) {
    this.dir = dir;
    Count = Math.Max(1, count);
  }

  /// <summary>The slot count.</summary>
  public int Count { get; }

  /// <summary>The shared pool: <c>~/.cache/vsshape/gate-slots</c>, <c>GATE_SLOTS</c> of them (the processor count
  /// when unset or not a number).</summary>
  public static Slots Shared() =>
    new(
      Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "vsshape",
        "gate-slots"
      ),
      int.TryParse(Environment.GetEnvironmentVariable("GATE_SLOTS"), out int n)
      && n > 0
        ? n
        : Environment.ProcessorCount
    );

  /// <summary>Takes a free slot without waiting; null when every slot is held.</summary>
  /// <exception cref="IOException">The slot directory cannot be used.</exception>
  public IDisposable? TryTake() {
    Directory.CreateDirectory(dir);
    for (int i = 0; i < Count; i++) {
      Held? h = Held.Open(Path.Combine(dir, i.ToString()));
      if (h != null)
        return h;
    }
    return null;
  }

  /// <summary>Takes a slot, polling until one is free.</summary>
  /// <exception cref="IOException">The slot directory cannot be used.</exception>
  public IDisposable Take() {
    while (true) {
      IDisposable? h = TryTake();
      if (h != null)
        return h;
      Thread.Sleep(20);
    }
  }

  [DllImport("libc", SetLastError = true)]
  private static extern int flock(int fd, int operation);

  [DllImport("libc", SetLastError = true)]
  private static extern int open(
    [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
    int flags,
    int mode
  );

  [DllImport("libc", SetLastError = true)]
  private static extern int close(int fd);

  private sealed class Held : IDisposable {
    private readonly FileStream? file;
    private readonly int fd;

    private Held(FileStream? file, int fd) {
      this.file = file;
      this.fd = fd;
    }

    public static Held? Open(string path) {
      if (!OperatingSystem.IsLinux()) {
        try {
          return new Held(
            new FileStream(
              path,
              FileMode.OpenOrCreate,
              FileAccess.ReadWrite,
              FileShare.None
            ),
            -1
          );
        } catch (IOException) {
          return null;
        }
      }
      int fd = open(path, OpenReadWriteCreate, 0x1A4);
      if (fd < 0)
        throw new IOException($"cannot open slot file {path}");
      if (flock(fd, LockEx | LockNb) == 0)
        return new Held(null, fd);
      close(fd);
      return null;
    }

    public void Dispose() {
      if (file != null) {
        file.Dispose();
        return;
      }
      flock(fd, LockUn);
      close(fd);
    }
  }
}
