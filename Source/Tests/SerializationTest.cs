using Multiplayer.API;
using Multiplayer.Client;
using Multiplayer.Common;

namespace Tests;

public class SerializationTest
{
    private static T? RoundtripSerialization<T>(SyncSerialization serialization, T? obj)
    {
        var writer = new ByteWriter();
        serialization.WriteSync(writer, obj);
        return serialization.ReadSync<T>(new ByteReader(writer.ToArray()));
    }

    [Test]
    public void SimpleString()
    {
        var ser = new SyncSerialization(new TestTypeHelper());
        Assert.That(RoundtripSerialization(ser, "abc"), Is.EqualTo("abc"));
    }

    [Test]
    public void StringList()
    {
        var ser = new SyncSerialization(new TestTypeHelper());
        List<string> input = ["abc", "def"];
        Assert.That(RoundtripSerialization(ser, input), Is.EqualTo(input));
    }

    [Test]
    public void NestedCollectionMatchesTheExistingWireFormat()
    {
        var serialization = new SyncSerialization(new TestTypeHelper());
        var input = new Dictionary<ushort, (ShortCode?, IEnumerable<int>)>
        {
            [0x1234] = (ShortCode.Negative, [0x01020304, -2])
        };

        byte[] recorded =
        [
            1, 0, 0x34, 0x12,   // The dictionary's key array.
            1, 0,               // The dictionary's value array.
            0, 0xFE, 0xFF,      // Non-null enum value.
            2, 0, 4, 3, 2, 1, 0xFE, 0xFF, 0xFF, 0xFF
        ];
        var writer = new ByteWriter();
        serialization.WriteSync(writer, input);
        Assert.That(writer.ToArray(), Is.EqualTo(recorded));

        var reader = new ByteReader(recorded);
        var result = serialization.ReadSync<Dictionary<ushort, (ShortCode?, IEnumerable<int>)>>(reader)!;
        Assert.Multiple(() =>
        {
            Assert.That(result[0x1234].Item1, Is.EqualTo(ShortCode.Negative));
            Assert.That(result[0x1234].Item2, Is.EqualTo(new[] { 0x01020304, -2 }));
            Assert.That(reader.Left, Is.Zero);
        });
    }

    [Test]
    public void NullCollectionsAndReferenceTuplesStillRoundTrip()
    {
        var serialization = new SyncSerialization(new TestTypeHelper());
        var tuple = Tuple.Create<ShortCode?, HashSet<int>?, string[]>(null, [5, 8], ["one", "two"]);
        var result = RoundtripSerialization(serialization, tuple)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Item1, Is.Null);
            Assert.That(result.Item2, Is.EquivalentTo(new[] { 5, 8 }));
            Assert.That(result.Item3, Is.EqualTo(tuple.Item3));
            Assert.That(RoundtripSerialization<HashSet<int>>(serialization, null), Is.Null);
            Assert.That(RoundtripSerialization<Dictionary<ushort, int>>(serialization, null), Is.Null);
        });
    }

    private enum ShortCode : short
    {
        Negative = -2
    }

    [Test]
    public void BasicSyncWorkers()
    {
        var ser = new SyncSerialization(new TestTypeHelper());

        // Construct sync tree
        ser.syncTree = new SyncWorkerDictionaryTree
        {
            {
                (ByteWriter writer, IA a) =>
                {
                    ser.WriteSync(writer, a is A);
                    ser.WriteSyncObject(writer, a, a.GetType());
                },
                reader =>
                {
                    if (ser.ReadSync<bool>(reader))
                        return ser.ReadSync<A>(reader)!;
                    return ser.ReadSync<Z>(reader)!;
                }
            },
            {
                (SyncWorker worker, ref A a) =>
                {
                    worker.Bind(ref a.a);
                }, false, true
            },
            {
                (SyncWorker worker, ref Z z) =>
                {
                    worker.Bind(ref z.z);
                }, false, true
            },
        };

        {
            var input = new A { a = 1 };

            // Sync A as A
            Assert.That(
                RoundtripSerialization(ser, input),
                Is.EqualTo(input)
            );

            // Sync A as IA
            Assert.That(
                RoundtripSerialization<IA>(ser, input),
                Is.EqualTo(input)
            );
        }

        {
            var input = new Z { z = 2 };

            // Sync Z as IA
            Assert.That(
                RoundtripSerialization<IA>(ser, input),
                Is.EqualTo(input)
            );
        }
    }

    [Test]
    public void ImplicitSyncWorker()
    {
        var ser = new SyncSerialization(new TestTypeHelper());

        ser.syncTree = new SyncWorkerDictionaryTree
        {
            {
                (SyncWorker worker, ref C3 c) =>
                {
                    worker.Bind(ref c.b);
                }, true, true
            },
            {
                (SyncWorker worker, ref C2 c) =>
                {
                    worker.Bind(ref c.a);
                }, true, true
            },
            {
                (SyncWorker worker, ref C1 c) =>
                {
                }, true, true
            },
        };

        var input = new C3 { a = 1 };
        Assert.That(
            RoundtripSerialization(ser, input),
            Is.EqualTo(input)
        );
    }

    [Test]
    public void SyncWithImpl()
    {
        var ser = new SyncSerialization(new TestTypeHelper());

        ser.syncTree = new SyncWorkerDictionaryTree
        {
            {
                (SyncWorker worker, ref C2 c) =>
                {
                    worker.Bind(ref c.a);
                }, false, true
            },
            {
                (SyncWorker worker, ref C3 c) =>
                {
                    worker.Bind(ref c.a);
                }, false, true
            },
        };

        ser.RegisterForSyncWithImpl(typeof(C1));

        {
            var input = new C2 { a = 1 };

            Assert.That(
                RoundtripSerialization<C1>(ser, input),
                Is.EqualTo(input)
            );
        }

        {
            var input = new C3 { a = 1 };

            Assert.That(
                RoundtripSerialization<C1>(ser, input),
                Is.EqualTo(input)
            );
        }
    }

    class TestTypeHelper : SyncTypeHelper
    {
        public override List<Type> GetImplementations(Type baseType)
        {
            if (baseType == typeof(C1))
                return [typeof(C2), typeof(C3)];

            return [];
        }
    }

    public class C1;

    public class C2 : C1
    {
        public int a;

        public override bool Equals(object? obj)
        {
            return obj is C2 c && c.a == a;
        }
    }

    public class C3 : C2
    {
        public int b;

        public override bool Equals(object? obj)
        {
            return obj is C3 c && c.b == b && c.a == a;
        }
    }

    public interface IA;

    // Using structs so that equality is by value which simplifies the test code
    public struct A : IA
    {
        public int a;
    }

    public struct Z : IA
    {
        public int z;
    }
}
