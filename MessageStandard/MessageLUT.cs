﻿using Megumin.Message;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Megumin.Remote
{
    /// <summary>
    /// Key冲突改怎么做
    /// </summary>
    public enum KeyAlreadyHave
    {
        /// <summary>
        /// 替换
        /// </summary>
        Replace,
        /// <summary>
        /// 跳过
        /// </summary>
        Skip,
        /// <summary>
        /// 抛出异常
        /// </summary>
        ThrowException,
    }

    /// <summary>
    /// 通用序列化库接口
    /// </summary>
    public interface IMeguminFormater
    {
        /// <summary>
        /// 消息识别ID
        /// </summary>
        int MessageID { get; }
        /// <summary>
        /// 消息类型
        /// </summary>
        Type BindType { get; }

        /// <summary>
        /// 序列化函数
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="value"></param>
        /// <param name="options"></param>
        /// <remarks>序列化函数不在提供序列化多少字节，需要在writer中自己统计</remarks>
        void Serialize(IBufferWriter<byte> writer, object value, object options = null);

        /// <summary>
        /// 反序列化函数
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>返回值不考虑泛型，泛型虽然能避免值类型消息装箱，但是调用时要使用反射去转化为
        /// 对应类型接口，在rpc回调转型处仍然会有类型匹配失败问题，得不偿失。</remarks>
        object Deserialize(in ReadOnlySpan<byte> source, object options = null);

        /// <summary>
        /// 反序列化函数
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>返回值不考虑泛型，泛型虽然能避免值类型消息装箱，但是调用时要使用反射去转化为
        /// 对应类型接口，在rpc回调转型处仍然会有类型匹配失败问题，得不偿失。</remarks>
        object Deserialize(in ReadOnlySequence<byte> source, object options = null);

        /// <summary>
        /// 反序列化函数
        /// <para></para>虽然ReadOnlyMemory可以转换为ReadOnlySpan，但是序列化库支持不一致，所以三个API都要
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>返回值不考虑泛型，泛型虽然能避免值类型消息装箱，但是调用时要使用反射去转化为
        /// 对应类型接口，在rpc回调转型处仍然会有类型匹配失败问题，得不偿失。</remarks>
        object Deserialize(in ReadOnlyMemory<byte> source, object options = null);
    }

    /// <summary>
    /// 不要使用协变，会导致序列化错误
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IMeguminFormater<T> : IMeguminFormater
    {
        /// <summary>
        /// 序列化函数
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="value"></param>
        /// <param name="options"></param>
        /// <remarks>序列化函数不在提供序列化多少字节，需要在writer中自己统计</remarks>
        void Serialize(IBufferWriter<byte> writer, T value, object options = null);
    }

    /// <summary>
    /// 对象自身就是序列化器，是MessageLut没注册时的fallback。
    /// </summary>
    [Obsolete("没有MessageLut根本就找不到类型，这个思路不成立。", true)]
    public interface IMeguminSelfFormater : IMeguminFormater
    {
        /// <summary>
        /// 先构造对象，然后自己解析。
        /// </summary>
        /// <param name="byteSequence"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        bool SelfDeserialize(in ReadOnlySequence<byte> byteSequence, object options = null);
    }

    /// <summary>
    /// 消息查找表
    /// </summary>
    public partial class MessageLUT
    {
        static readonly Dictionary<int, IMeguminFormater> IDDic = new Dictionary<int, IMeguminFormater>();
        static readonly Dictionary<Type, IMeguminFormater> TypeDic = new Dictionary<Type, IMeguminFormater>();

        static MessageLUT()
        {
            //注册基础类型
            Regist(new StringFormatter());
            Regist(new IntFormatter());
            Regist(new FloatFormatter());
            Regist(new LongFormatter());
            Regist(new DoubleFormatter());
            Regist(new DatetimeFormatter());
            Regist(new DatetimeOffsetFormatter());
            Regist(new ByteArrayFormatter());

            //注册内置消息
            Regist(new TestPacket1());
            Regist(new TestPacket2());
            Regist(new TestPacket3());
            Regist(new TestPacket4());
            Regist(new Heartbeat());
            Regist(new GetTime());
            Regist(new Authentication());
        }

        /// <summary>
        /// 注册序列化器
        /// </summary>
        /// <param name="meguminFormater"></param>
        /// <param name="key"></param>
        public static void Regist(IMeguminFormater meguminFormater, KeyAlreadyHave key = KeyAlreadyHave.Skip)
        {
            if (meguminFormater.BindType == null)
            {
                throw new ArgumentException("序列化器没有绑定类型");
            }

            switch (key)
            {
                case KeyAlreadyHave.Replace:

                    if (IDDic.TryGetValue(meguminFormater.MessageID, out var old))
                    {
                        IDDic.Remove(old.MessageID);
                        TypeDic.Remove(old.BindType);
                    }

                    if (TypeDic.TryGetValue(meguminFormater.BindType, out var old2))
                    {
                        IDDic.Remove(old2.MessageID);
                        TypeDic.Remove(old2.BindType);
                    }
                    IDDic[meguminFormater.MessageID] = meguminFormater;
                    TypeDic[meguminFormater.BindType] = meguminFormater;

                    break;
                case KeyAlreadyHave.Skip:
                    if (IDDic.ContainsKey(meguminFormater.MessageID)
                         || TypeDic.ContainsKey(meguminFormater.BindType))
                    {
                        return;
                    }

                    IDDic[meguminFormater.MessageID] = meguminFormater;
                    TypeDic[meguminFormater.BindType] = meguminFormater;
                    break;
                case KeyAlreadyHave.ThrowException:
                    if (IDDic.ContainsKey(meguminFormater.MessageID))
                    {
                        throw new ArgumentException
                            ($"消息ID冲突，同一个ID再次注册。 当前ID:{meguminFormater.MessageID}。 当前类型:{meguminFormater.BindType.FullName}。" +
                            $"已有类型：{IDDic[meguminFormater.MessageID].BindType.FullName}");
                    }

                    if (TypeDic.ContainsKey(meguminFormater.BindType))
                    {
                        throw new ArgumentException
                            ($"消息类型冲突，同一个类型再次注册。当前类型:{meguminFormater.BindType.FullName}。 当前ID:{meguminFormater.MessageID}。" +
                            $"已有ID：{TypeDic[meguminFormater.BindType].MessageID}。");
                    }
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 注册序列化器
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        public static void RegistIMeguminFormater<T>(KeyAlreadyHave key = KeyAlreadyHave.Skip) where T : class, IMeguminFormater, new()
        {
            T f = new T();
            Regist(f, key);
        }

        /// <summary>
        /// 查找消息类型
        /// </summary>
        /// <param name="messageID"></param>
        /// <returns></returns>
        public static Type GetType(int messageID)
        {
            return IDDic[messageID].BindType;
        }

        /// <summary>
        /// 查找消息类型
        /// </summary>
        /// <param name="messageID"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public static bool TryGetType(int messageID, out Type type)
        {
            if (IDDic.TryGetValue(messageID, out var res))
            {
                type = res.BindType;
                return true;
            }
            else
            {
                type = null;
                return false;
            }
        }

        /// <summary>
        /// 查找消息ID
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static int GetID<T>()
        {
            var type = typeof(T);
            var formater = TypeDic[type];
            return formater.MessageID;
        }

        /// <summary>
        /// 查找消息ID
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static int GetID(Type type)
        {
            var formater = TypeDic[type];
            return formater.MessageID;
        }

        /// <summary>
        /// 查找消息ID
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="ID"></param>
        /// <returns></returns>
        public static bool TryGetID<T>(out int ID)
        {
            if (TypeDic.TryGetValue(typeof(T), out var res))
            {
                ID = res.MessageID;
                return true;
            }

            ID = -1;
            return false;
        }

        /// <summary>
        /// 查找消息ID
        /// </summary>
        /// <param name="type"></param>
        /// <param name="ID"></param>
        /// <returns></returns>
        public static bool TryGetID(Type type, out int ID)
        {
            if (TypeDic.TryGetValue(type, out var res))
            {
                ID = res.MessageID;
                return true;
            }

            ID = -1;
            return false;
        }

        public static bool TryGetFormater(Type type, out IMeguminFormater formater)
        {
            return TypeDic.TryGetValue(type, out formater);
        }

        public static bool TryGetFormater(int messageID, out IMeguminFormater formater)
        {
            return IDDic.TryGetValue(messageID, out formater);
        }
    }

    public partial class MessageLUT
    {
        ///// <summary>
        ///// 序列化一个对象到指定writer
        ///// </summary>
        ///// <param name="writer"></param>
        ///// <param name="value"></param>
        ///// <param name="options"></param>
        ///// <returns>消息ID</returns>
        ///// <remarks>序列化函数不在提供序列化多少字节，需要在writer中自己统计</remarks>
        ///// <exception cref="KeyNotFoundException"></exception>
        ///// <exception cref="ArgumentNullException"></exception>
        //public static int Serialize(IBufferWriter<byte> writer, object value, object options = null)
        //{
        //    var type = value.GetType();
        //    var formater = TypeDic[type];
        //    formater.Serialize(writer, value, options);
        //    return formater.MessageID;
        //}

        /// <summary>
        /// 序列化一个对象到指定writer
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="value"></param>
        /// <param name="options"></param>
        /// <returns>消息ID</returns>
        /// <remarks>序列化函数不在提供序列化多少字节，需要在writer中自己统计</remarks>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public static int Serialize<T>(IBufferWriter<byte> writer, T value, object options = null)
        {
            //这里一定要从value获取真实类型，防止类型隐式转型导致类型推导不正确，所以不能用typeof(T)。
            //使用泛型的目的时尽可能的减少装箱。
            //var testtype = typeof(T);
            var type = value.GetType();

            var formater = TypeDic[type];
            if (formater is IMeguminFormater<T> gformater)
            {
                gformater.Serialize(writer, value, options);
            }
            else
            {
                formater.Serialize(writer, value, options);
            }

            return formater.MessageID;
        }
    }

    public partial class MessageLUT
    {
        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="messageID"></param>
        /// <param name="byteSequence"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public static object Deserialize(int messageID, in ReadOnlySequence<byte> byteSequence, object options = null)
        {
            var formater = IDDic[messageID];
            var result = formater.Deserialize(byteSequence, options);
            return result;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="messageID"></param>
        /// <param name="byteSequence"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public static object Deserialize(int messageID, in ReadOnlySpan<byte> byteSequence, object options = null)
        {
            var formater = IDDic[messageID];
            var result = formater.Deserialize(byteSequence, options);
            return result;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="messageID"></param>
        /// <param name="byteSequence"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public static object Deserialize(int messageID, in ReadOnlyMemory<byte> byteSequence, object options = null)
        {
            var formater = IDDic[messageID];
            var result = formater.Deserialize(byteSequence, options);
            return result;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>有时即使类型不匹配也能反序列化成功，但得到的值时错误的</remarks>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidCastException"></exception>
        public static T Deserialize<T>(in ReadOnlySequence<byte> source, object options = null)
        {
            var type = typeof(T);
            var formater = TypeDic[type];
            var result = formater.Deserialize(source, options);
            return (T)result;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>有时即使类型不匹配也能反序列化成功，但得到的值时错误的</remarks>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidCastException"></exception>
        public static T Deserialize<T>(in ReadOnlySpan<byte> source, object options = null)
        {
            var type = typeof(T);
            var formater = TypeDic[type];
            var result = formater.Deserialize(source, options);
            return (T)result;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        /// <param name="source"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <remarks>有时即使类型不匹配也能反序列化成功，但得到的值时错误的</remarks>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidCastException"></exception>
        public static T Deserialize<T>(in ReadOnlyMemory<byte> source, object options = null)
        {
            var type = typeof(T);
            var formater = TypeDic[type];
            var result = formater.Deserialize(source, options);
            return (T)result;
        }
    }

    public partial class MessageLUT
    {
        public static T TestType<T>(T original)
        {
            MessageLUTTestBuffer wr = new MessageLUTTestBuffer();
            MessageLUT.Serialize(wr, original);
            return MessageLUT.Deserialize<T>(wr.ReadOnlySpan);
        }
    }

    /// <summary>
    /// 用于反序列化时获取长度
    /// </summary>
    public interface IDeserializeLengthWriter
    {
        int Length { set; }
    }

    /// <summary>
    /// 用于反序列化时获取长度
    /// </summary>
    public class DeserializeLengthHelper : IDeserializeLengthWriter
    {
        //https://learn.microsoft.com/en-us/dotnet/api/system.threadstaticattribute?redirectedfrom=MSDN&view=net-6.0
        //[ThreadStatic]
        //static DeserializeLengthHelper defaulthelper;
        //public static DeserializeLengthHelper Default
        //{
        //    get
        //    {
        //        if (defaulthelper == null)
        //        {
        //            defaulthelper = new DeserializeLengthHelper();
        //        }
        //        return defaulthelper;
        //    }
        //}

        //https://stackoverflow.com/questions/18333885/threadstatic-v-s-threadlocalt-is-generic-better-than-attribute
        static readonly ThreadLocal<DeserializeLengthHelper> defaulthelper
            = new ThreadLocal<DeserializeLengthHelper>(static () => new DeserializeLengthHelper());

        public static DeserializeLengthHelper Default => defaulthelper.Value;

        //仅将Length标记为ThreadStatic是不够的，可能在一个线程写，在另一个线程读，造成错误。
        public int Length { get; set; }
    }

    /// <summary>
    /// 包装<see cref="IBufferWriter{T}"/><see cref="byte"/>成一个长度无限的只写流，
    /// 只有<see cref="Write(byte[], int, int)"/>函数起作用。
    /// </summary>
    public class BufferWriterBytesSteam : Stream
    {
        public IBufferWriter<byte> BufferWriter { get; set; }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var destination = BufferWriter.GetSpan(count);
            var span = new Span<byte>(buffer, offset, count);
            span.CopyTo(destination);
            BufferWriter.Advance(count);
        }

        public override bool CanRead { get; } = false;
        public override bool CanSeek { get; } = false;
        public override bool CanWrite { get; } = true;
        public override long Length { get; } = long.MaxValue;
        public override long Position { get; set; }
    }
}
