﻿/*
 * This file is subject to the terms and conditions defined in
 * file 'license.txt', which is part of this source code package.
 */



using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SteamKit2
{

    class TcpConnection : Connection
    {
        const uint MAGIC = 0x31305456; // "VT01"

        Socket sock;

        volatile bool wantsNetShutdown;
        NetworkStream netStream;
        ReaderWriterLockSlim netLock;

        BinaryReader netReader;
        BinaryWriter netWriter;

        Thread netThread;

        public TcpConnection() : base()
        {
            netLock = new ReaderWriterLockSlim( LockRecursionPolicy.NoRecursion );
        }

        /// <summary>
        /// Connects to the specified end point.
        /// </summary>
        /// <param name="endPoint">The end point.</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        public override void Connect( IPEndPoint endPoint, int timeout )
        {
            // if we're connected, disconnect
            Disconnect();

            Socket socket = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
            DebugLog.WriteLine( "TcpConnection", "Connecting to {0}...", endPoint );

            ThreadPool.QueueUserWorkItem( sender =>
            {
                var asyncResult = socket.BeginConnect( endPoint, null, null );

                if ( asyncResult.AsyncWaitHandle.WaitOne( timeout ) )
                {
                    sock = socket;
                    ConnectCompleted( socket, asyncResult );
                }
                else
                {
                    socket.Close();
                    ConnectCompleted( null, asyncResult );
                }
            });
        }

        void ConnectCompleted( Socket sock, IAsyncResult asyncResult )
        {
            if ( sock == null )
            {
                DebugLog.WriteLine( "TcpConnection", "Timed out while connecting" );
                OnDisconnected( EventArgs.Empty );
                return;
            }

            try
            {
                sock.EndConnect( asyncResult );
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine( "TcpConnection", "Socket exception while connecting: {0}", ex );
                OnDisconnected( EventArgs.Empty );
                return;
            }

            netLock.EnterWriteLock();

            try
            {
                if ( !sock.Connected )
                {
                    DebugLog.WriteLine( "TcpConnection", "Unable to connect" );
                    OnDisconnected( EventArgs.Empty );
                    return;
                }

                DebugLog.WriteLine( "TcpConnection", "Connected!" );

                wantsNetShutdown = false;
                netStream = new NetworkStream( sock, false );

                netReader = new BinaryReader( netStream );
                netWriter = new BinaryWriter( netStream );

                // initialize our network thread
                netThread = new Thread( NetLoop );
                netThread.Name = "TcpConnection Thread";
                netThread.Start( sock );
            }
            finally
            {
                netLock.ExitWriteLock();
            }

            OnConnected( EventArgs.Empty );
        }

        /// <summary>
        /// Disconnects this instance.
        /// </summary>
        public override void Disconnect()
        {
            Cleanup();            
        }

        /// <summary>
        /// Sends the specified client net message.
        /// </summary>
        /// <param name="clientMsg">The client net message.</param>
        public override void Send( IClientMsg clientMsg )
        {
            byte[] data = clientMsg.Serialize();

            // encrypt outgoing traffic if we need to
            if ( NetFilter != null )
            {
                data = NetFilter.ProcessOutgoing( data );
            }

            // a Send from the netThread has the potential to acquire the read lock and block while Disconnect is trying to join us
            while ( !wantsNetShutdown && !netLock.TryEnterReadLock( 500 ) ) { }

            try
            {
                if ( wantsNetShutdown || netStream == null )
                {
                    DebugLog.WriteLine( "TcpConnection", "Attempting to send client message when not connected: {0}", clientMsg.MsgType );
                    return;
                }

                // need to ensure ordering between concurrent Sends
                lock ( netWriter )
                {
                    // write header
                    netWriter.Write( (uint)data.Length );
                    netWriter.Write( TcpConnection.MAGIC );

                    netWriter.Write( data );
                }
            }
            finally
            {
                if ( netLock.IsReadLockHeld )
                    netLock.ExitReadLock();
            }
        }

        // this is now a steamkit meme
        /// <summary>
        /// Nets the loop.
        /// </summary>
        void NetLoop( object param )
        {
            // poll for readable data every 100ms
            const int POLL_MS = 100;
            Socket socket = param as Socket;

            while ( !wantsNetShutdown )
            {
                bool canRead = false;

                try
                {
                    canRead = socket.Poll( POLL_MS * 1000, SelectMode.SelectRead );
                }
                catch ( Exception ex )
                {
                    DebugLog.WriteLine( "TcpConnection", "Socket exception while polling: {0}", ex );

                    Cleanup();
                    return;
                }

                if ( !canRead )
                {
                    // nothing to read yet
                    continue;
                }

                // potential here is to be waiting to acquire the lock when Disconnect is trying to join us
                while ( !wantsNetShutdown && !netLock.TryEnterUpgradeableReadLock( 500 ) ) { }

                byte[] packData = null;

                try
                {
                    if ( wantsNetShutdown || netStream == null )
                    {
                        return;
                    }

                    // read the packet off the network
                    packData = ReadPacket();
                }
                catch ( IOException ex )
                {
                    DebugLog.WriteLine( "TcpConnection", "Socket exception occurred while reading packet: {0}", ex );

                    // signal that our connection is dead
                    Cleanup();
                    return;
                }
                finally
                {
                    if( netLock.IsUpgradeableReadLockHeld )
                        netLock.ExitUpgradeableReadLock();
                }

                // decrypt the data off the wire if needed
                if ( NetFilter != null )
                {
                    packData = NetFilter.ProcessIncoming( packData );
                }

                OnNetMsgReceived( new NetMsgEventArgs( packData, socket.RemoteEndPoint as IPEndPoint ) );
            }
        }


        byte[] ReadPacket()
        {
            // the tcp packet header is considerably less complex than the udp one
            // it only consists of the packet length, followed by the "VT01" magic
            uint packetLen = 0;
            uint packetMagic = 0;

            try
            {
                packetLen = netReader.ReadUInt32();
                packetMagic = netReader.ReadUInt32();
            }
            catch ( IOException ex )
            {
                throw new IOException( "Connection lost while reading packet header.", ex );
            }

            if ( packetMagic != TcpConnection.MAGIC )
            {
                throw new IOException( "Got a packet with invalid magic!" );
            }

            // rest of the packet is the physical data
            byte[] packData = netReader.ReadBytes( (int)packetLen );

            if ( packData.Length != packetLen )
            {
                throw new IOException( "Connection lost while reading packet payload" );
            }

            return packData;
        }

        void Cleanup()
        {
            while ( !wantsNetShutdown && !netLock.TryEnterWriteLock( 500 ) ) { }

            // no point in continuing if we caught an error inside netThread while shutting down
            if ( wantsNetShutdown ) return;

            try
            {
                if ( netThread != null )
                {
                    if ( Thread.CurrentThread.ManagedThreadId != netThread.ManagedThreadId )
                    {
                        wantsNetShutdown = true;
                        // wait for our network thread to terminate
                        netThread.Join();
                    }

                    netThread = null;
                    OnDisconnected( EventArgs.Empty );
                }

                // cleanup streams
                if ( netReader != null )
                {
                    netReader.Dispose();
                    netReader = null;
                }

                if ( netWriter != null )
                {
                    netWriter.Dispose();
                    netWriter = null;
                }

                if ( netStream != null )
                {
                    netStream.Dispose();
                    netStream = null;
                }

                if ( sock != null )
                {
                    try
                    {
                        // cleanup socket
                        if ( sock.Connected )
                        {
                            sock.Shutdown( SocketShutdown.Both );
                            sock.Disconnect( true );
                        }
                        sock.Close();
                    }
                    catch
                    {
                        // Shutdown is throwing when the remote end closes the connection before SteamKit attempts to
                        // so this should be safe as a no-op
                        // see: https://bitbucket.org/VoiDeD/steamre/issue/41/socketexception-thrown-when-closing
                    }

                    sock = null;
                }
            }
            finally
            {
                netLock.ExitWriteLock();
            }
        }


        /// <summary>
        /// Gets the local IP.
        /// </summary>
        /// <returns>The local IP.</returns>
        public override IPAddress GetLocalIP()
        {
            while ( !wantsNetShutdown && !netLock.TryEnterReadLock( 500 ) ) { }

            try
            {
                if ( wantsNetShutdown || sock == null ) return null;

                return NetHelpers.GetLocalIP( sock );
            }
            finally
            {
                if ( netLock.IsReadLockHeld )
                    netLock.ExitReadLock();
            }
        }
    }
}