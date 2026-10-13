using System.Runtime.CompilerServices;
using System.Text;
using Rambutan.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// The freedesktop Secret Service over the session bus: what libsecret wraps,
/// spoken directly with Tmds.DBus.Protocol, which Avalonia already ships for its
/// own Linux integration. So there is no native libsecret dependency for an
/// AppImage to miss, and every call has a time limit.
/// <para>
/// Secrets travel in the "plain" session algorithm. The session bus is private
/// to this user, and any process that can listen on it can also ask the keyring
/// for the item directly, so the Diffie-Hellman transport adds no protection
/// here. At rest the keyring encrypts them.
/// </para>
/// </summary>
public sealed class SecretServiceStore : ISecretStore, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string NoObject = "/";

    /// <summary>One call; activating the daemon is the slowest case.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>An unlock or create prompt waits for the user to type a password.</summary>
    internal static TimeSpan PromptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    private readonly DBusConnection _connection;
    private readonly string _session;
    private readonly string _defaultCollectionLabel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SecretServiceStore(DBusConnection connection, string session, string defaultCollectionLabel)
    {
        _connection = connection;
        _session = session;
        _defaultCollectionLabel = defaultCollectionLabel;
    }

    /// <summary>
    /// Connects to the Secret Service on the session bus, or returns null (and
    /// logs why) when there is no session bus or nothing provides the service.
    /// </summary>
    /// <param name="defaultCollectionLabel">The name for the default keyring, should
    /// the service have none and need one created for the first write.</param>
    public static Task<SecretServiceStore?> ConnectAsync(string defaultCollectionLabel) =>
        // On the thread pool: the connection's message loop must not be tied to
        // the caller's synchronization context, or a blocking call made on that
        // context later would wait for a reply the blocked context has to deliver.
        Task.Run(() => ConnectCoreAsync(defaultCollectionLabel));

    private static async Task<SecretServiceStore?> ConnectCoreAsync(string defaultCollectionLabel)
    {
        if (DBusAddress.Session is not { Length: > 0 } address)
        {
            AppLog.Write("keyring", "No D-Bus session bus; no system keyring.");
            return null;
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
            var running = await connection.ListServicesAsync().WaitAsync(CallTimeout).ConfigureAwait(false);
            if (!running.Contains(Service))
            {
                var activatable = await connection.ListActivatableServicesAsync().WaitAsync(CallTimeout).ConfigureAwait(false);
                if (!activatable.Contains(Service))
                {
                    AppLog.Write("keyring", "Nothing provides the Secret Service on the session bus; no system keyring.");
                    connection.Dispose();
                    return null;
                }
            }

            var session = await CallAsync(connection, ServicePath, ServiceInterface, "OpenSession", "sv",
                static (ref MessageWriter w) =>
                {
                    w.WriteString("plain");
                    w.WriteVariantString("");
                },
                static (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                }).ConfigureAwait(false);
            AppLog.Write("keyring", "Connected to the Secret Service.");
            return new SecretServiceStore(connection, session, defaultCollectionLabel);
        }
        catch (Exception e)
        {
            AppLog.Write("keyring", $"The Secret Service didn't answer ({Describe(e)}); no system keyring.");
            connection.Dispose();
            return null;
        }
    }

    public string? Read(IReadOnlyDictionary<string, string> attributes) => Run(async () =>
    {
        var (unlocked, locked) = await SearchAsync(attributes).ConfigureAwait(false);
        string item;
        if (unlocked.Length > 0)
        {
            item = unlocked[0];
        }
        else if (locked.Length > 0)
        {
            await UnlockAsync(locked).ConfigureAwait(false);
            item = locked[0];
        }
        else
        {
            return null;
        }

        var bytes = await CallAsync(_connection, item, ItemInterface, "GetSecret", "o",
            (ref MessageWriter w) => w.WriteObjectPath(_session),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();          // parameters: empty for "plain"
                return reader.ReadArrayOfByte();   // the value
            }).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    });

    public void Write(IReadOnlyDictionary<string, string> attributes, string label, string secret) => Run(async () =>
    {
        var collection = await DefaultCollectionAsync().ConfigureAwait(false);
        await UnlockAsync([collection]).ConfigureAwait(false);

        var value = Encoding.UTF8.GetBytes(secret);
        var prompt = await CallAsync(_connection, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b",
            (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("org.freedesktop.Secret.Item.Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEntryStart();
                w.WriteString("org.freedesktop.Secret.Item.Attributes");
                w.WriteSignature("a{ss}");
                WriteAttributes(ref w, attributes);
                w.WriteDictionaryEnd(properties);

                w.WriteStructureStart();
                w.WriteObjectPath(_session);
                w.WriteArray(Array.Empty<byte>());
                w.WriteArray(value);
                w.WriteString("text/plain; charset=utf8");

                w.WriteBool(true);   // replace the item with the same attributes
            },
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadObjectPath();
                return reader.ReadObjectPathAsString();
            }).ConfigureAwait(false);
        if (prompt != NoObject)
            await PromptAsync(prompt, "storing the secret").ConfigureAwait(false);
        return true;
    });

    public void Delete(IReadOnlyDictionary<string, string> attributes) => Run(async () =>
    {
        var (unlocked, locked) = await SearchAsync(attributes).ConfigureAwait(false);
        if (locked.Length > 0)
            await UnlockAsync(locked).ConfigureAwait(false);
        foreach (var item in unlocked.Concat(locked))
        {
            var prompt = await CallAsync(_connection, item, ItemInterface, "Delete", null, null,
                static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
            if (prompt != NoObject)
                await PromptAsync(prompt, "deleting the secret").ConfigureAwait(false);
        }
        return true;
    });

    public void Dispose() => _connection.Dispose();

    /// <summary>The collection behind the "default" alias, created (which may
    /// prompt for a new keyring password) when the service has none yet, as
    /// libsecret does.</summary>
    private async Task<string> DefaultCollectionAsync()
    {
        var collection = await CallAsync(_connection, ServicePath, ServiceInterface, "ReadAlias", "s",
            static (ref MessageWriter w) => w.WriteString("default"),
            static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        if (collection != NoObject)
            return collection;

        AppLog.Write("keyring", "The keyring has no default collection; creating one.");
        var label = _defaultCollectionLabel;
        var (created, prompt) = await CallAsync(_connection, ServicePath, ServiceInterface, "CreateCollection", "a{sv}s",
            (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("org.freedesktop.Secret.Collection.Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEnd(properties);
                w.WriteString("default");
            },
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
            }).ConfigureAwait(false);
        if (prompt == NoObject)
            return created;
        var result = await PromptAsync(prompt, "creating the default keyring").ConfigureAwait(false);
        return result.GetObjectPathAsString();
    }

    private Task<(string[] Unlocked, string[] Locked)> SearchAsync(IReadOnlyDictionary<string, string> attributes) =>
        CallAsync(_connection, ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter w) => WriteAttributes(ref w, attributes),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                var locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                return (unlocked, locked);
            });

    /// <summary>Unlocks the objects, prompting when the service asks to.</summary>
    private async Task UnlockAsync(string[] objects)
    {
        var (_, prompt) = await CallAsync(_connection, ServicePath, ServiceInterface, "Unlock", "ao",
            (ref MessageWriter w) => w.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray()),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadArrayOfObjectPath(), reader.ReadObjectPathAsString());
            }).ConfigureAwait(false);
        if (prompt != NoObject)
            await PromptAsync(prompt, "unlocking the keyring").ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the service's prompt (its own window, for a password) and waits for
    /// the answer. Dismissed, or unanswered within <see cref="PromptTimeout"/>
    /// (no prompter on a headless session), is a <see cref="SecretStoreException"/>.
    /// </summary>
    private async Task<VariantValue> PromptAsync(string prompt, string purpose)
    {
        var completed = new TaskCompletionSource<(bool Dismissed, VariantValue Result)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribed before Prompt is called, so a quick answer can't be missed.
        using var watch = await _connection.WatchSignalAsync(null, prompt, PromptInterface, "Completed",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var dismissed = reader.ReadBool();
                return (dismissed, reader.ReadVariantValue());
            },
            (Notification<(bool, VariantValue)> n) =>
            {
                if (n.HasValue)
                    completed.TrySetResult(n.Value);
                else if (n.Exception is { } e)
                    completed.TrySetException(e);
                else
                    completed.TrySetException(new SecretStoreException("The keyring went away during a prompt."));
            },
            ObserverFlags.None, false, null).AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        await LeaveTheReceiveLoop();

        await CallAsync(_connection, prompt, PromptInterface, "Prompt", "s",
            static (ref MessageWriter w) => w.WriteString(""),   // no parent window id
            static (_, _) => true).ConfigureAwait(false);

        (bool Dismissed, VariantValue Result) answer;
        try
        {
            answer = await completed.Task.WaitAsync(PromptTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Leave no prompt window behind for an operation that has given up.
            try
            {
                await CallAsync(_connection, prompt, PromptInterface, "Dismiss", null, null,
                    static (_, _) => true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
            throw new SecretStoreException($"Nobody answered the keyring's prompt for {purpose}.");
        }
        if (answer.Dismissed)
            throw new SecretStoreException($"The keyring's prompt for {purpose} was dismissed.");
        return answer.Result;
    }

    private static void WriteAttributes(ref MessageWriter w, IReadOnlyDictionary<string, string> attributes)
    {
        var dictionary = w.WriteDictionaryStart();
        foreach (var (key, value) in attributes)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(key);
            w.WriteString(value);
        }
        w.WriteDictionaryEnd(dictionary);
    }

    private delegate void WriteArgs(ref MessageWriter writer);

    private static async Task<T> CallAsync<T>(DBusConnection connection, string path, string @interface, string member,
        string? signature, WriteArgs? write, MessageValueReader<T> read)
    {
        MessageBuffer message;
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, @interface, member, signature, MessageFlags.None);
            write?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        var result = await connection.CallMethodAsync(message, read, null).WaitAsync(CallTimeout).ConfigureAwait(false);
        await LeaveTheReceiveLoop();
        return result;
    }

    /// <summary>
    /// A reply completes its call on the connection's receive loop, and an await
    /// continues right there. Code that then blocks (<see cref="Run{T}"/>, or a
    /// caller waiting on a result) would stall the loop that has to deliver the
    /// next reply: a deadlock. So every await on the connection moves to the
    /// thread pool before going on. (There is no synchronization context here:
    /// everything runs inside Task.Run.)
    /// </summary>
    private static YieldAwaitable LeaveTheReceiveLoop() => Task.Yield();

    /// <summary>One operation at a time, on the thread pool (no UI context to
    /// deadlock on), every failure as a <see cref="SecretStoreException"/>.</summary>
    private T Run<T>(Func<Task<T>> operation)
    {
        _gate.Wait();
        try
        {
            return Task.Run(operation).GetAwaiter().GetResult();
        }
        catch (SecretStoreException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SecretStoreException(Describe(e), e);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Describe(Exception e) => e switch
    {
        DBusErrorReplyException reply => $"{reply.ErrorName}: {reply.ErrorMessage}",
        TimeoutException => "no answer in time",
        _ => e.Message,
    };
}
