﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Manage linear memory segments to avoid re-create array buffer in heap memory
    /// Do not share same memory store with diferent files
    /// [ThreadSafe]
    /// </summary>
    internal class MemoryCache : IDisposable
    {
        /// <summary>
        /// Contains free ready-to-use pages in memory
        /// - All pages here MUST be ShareCounter = 0
        /// - All pages here MUST be Position = MaxValue
        /// </summary>
        private readonly ConcurrentQueue<PageBuffer> _free = new ConcurrentQueue<PageBuffer>();

        /// <summary>
        /// Contains only clean pages (from both data/log file) - support page concurrency use
        /// - MUST have defined Origin and Position
        /// - Contains only 1 instance per Position/Origin
        /// - Contains only pages with ShareCounter >= 0
        /// *  = 0 - Page are avaiable but are not using by anyone (can be moved into _free list in next Extend())
        /// * >= 1 - Page are in used by 1 or more threads. Each page use must run "Release" when finish
        /// </summary>
        private readonly ConcurrentDictionary<long, PageBuffer> _readable = new ConcurrentDictionary<long, PageBuffer>();

        /// <summary>
        /// Contains only writable (exclusive) pages. Can be loaded data from (data/log/new page) - no page concurrency
        /// - Can be a clean page (NewPage - no Position/Origin) or a COPIED page from disk (or _readable) (have Position + Origin)
        /// - Has no reference (byte[] ref) with any _readable
        /// - Contains pages with ShareCounter >= -1
        /// -  = -1 - This page are in use and can be changed content.
        /// -  =  0 - This page are not used by anyone (can be moved into _free list in next Extend())
        /// -  >= 1 - This page are in used by another thread. Can be in WriterQueue (that decrement this counter) or just
        ///           waiting next Release()
        /// </summary>
        private readonly ConcurrentDictionary<int, PageBuffer> _writable = new ConcurrentDictionary<int, PageBuffer>();

        /// <summary>
        /// Locker for multi extend()
        /// </summary>
        private readonly object _locker = new object();

        /// <summary>
        /// Get how many extends was made in this store
        /// </summary>
        private int _extends = 0;

        public MemoryCache()
        {
            this.Extend();
        }

        #region Readable Pages

        /// <summary>
        /// Get page from clean cache (readable). If page not exits, create this new page and load data using factory fn
        /// </summary>
        public PageBuffer GetReadablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            // get dict key based on position/origin
            var key = this.GetReadableKey(position, origin);

            // try get from _readble dict or create new
            var page = _readable.GetOrAdd(key, (k) =>
            {
                // get new page from _free pages (or extend)
                var newPage = this.GetFreePage();

                newPage.Position = position;
                newPage.Origin = origin;

                // load page content with disk stream
                factory(position, newPage);

                return newPage;
            });

            // update LRU
            Interlocked.Exchange(ref page.Timestamp, DateTime.UtcNow.Ticks);

            // increment share counter
            Interlocked.Increment(ref page.ShareCounter);

            return page;
        }

        /// <summary>
        /// Get unique position in dictionary according with origin. Use positive/negative values
        /// </summary>
        private long GetReadableKey(long position, FileOrigin origin)
        {
            ENSURE(origin != FileOrigin.None, "file origin must be defined");

            if (origin == FileOrigin.Data)
            {
                return position;
            }
            else
            {
                if (position == 0) return long.MinValue;

                return -position;
            }
        }

        #endregion

        #region Writable Pages

        /// <summary>
        /// Request for a writable page - no other can read this page and this page has no reference
        /// Writable pages can be write or just released (with no write)
        /// Before Dispose, a writable page must be marked as clean
        /// </summary>
        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            // write pages always contains a new buffer array
            var writable = this.NewPage(position, origin, false);
            var key = this.GetReadableKey(position, origin);

            // if requested page already in cache, just copy buffer and avoid load from stream
            if (_readable.TryGetValue(key, out var clean))
            {
                Buffer.BlockCopy(clean.Array, clean.Offset, writable.Array, writable.Offset, PAGE_SIZE);
            }
            else
            {
                factory(position, writable);
            }

            return writable;
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable. Add into _writable list
        /// </summary>
        public PageBuffer NewPage()
        {
            return this.NewPage(long.MaxValue, FileOrigin.None, true);
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable. Add into _writable dict
        /// </summary>
        private PageBuffer NewPage(long position, FileOrigin origin, bool clear)
        {
            var page = this.GetFreePage();

            // set page position and page as writable
            page.Position = position;

            // define as writable
            page.ShareCounter = BUFFER_WRITABLE;

            // Timestamp = 0 means this page was never used (do not clear)
            if (clear && page.Timestamp > 0)
            {
                Array.Clear(page.Array, page.Offset, page.Count);
            }

            page.Origin = origin;
            page.Timestamp = DateTime.UtcNow.Ticks;

            // add into writable dict
            _writable.TryAdd(page.UniqueID, page);

            return page;
        }

        /// <summary>
        /// Try move page from Writable list to readable list. If page already in _readable, keeps page in _writable
        /// </summary>
        public bool TryMoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "Page must have a position");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "Page must be writable");
            ENSURE(page.Origin != FileOrigin.None, "page must has defined origin");

            var key = this.GetReadableKey(page.Position, page.Origin);

            // set shareCounter to 1 (is in use) - there is no concurrency in write
            page.ShareCounter = 1;

            if(_readable.TryAdd(key, page))
            {
                // there is no need to this 2 operation be concurrent (add in _readable + remove in _writable)
                // because writable page has no concurrency
                var removed = _writable.TryRemove(page.UniqueID, out var dummy);

                ENSURE(removed, "page must be removed from _writable list");

                return true;
            }
            else
            {
                // if page already in _readable, ok, keeps in _writable
                page.Timestamp = 1; // be first page to be re-used on next Extend()

                return false;
            }
        }

        /// <summary>
        /// Move page from Writable dict to Readable dict - if already exisits, override content
        /// Returns readable page
        /// </summary>
        public PageBuffer MoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "page must have position to be readable");
            ENSURE(page.Origin != FileOrigin.None, "page should be a source before move to readable");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "page must be writable before from to readable dict");

            var key = this.GetReadableKey(page.Position, page.Origin);

            // no concurrency in writable page
            // share counter must set in 2 because will be released twice (on write/on page release) 
            page.ShareCounter = 2;

            var readable = _readable.AddOrUpdate(key, page, (newKey, current) =>
            {
                ENSURE(current.ShareCounter == 0, "user must ensure this page are not in use when mark as read only");

                current.ShareCounter = 2;

                // if page already in cache, this is a duplicate page in memory
                // must update cached page with new page content
                Buffer.BlockCopy(page.Array, page.Offset, current.Array, current.Offset, PAGE_SIZE);

                return current;
            });

            var removed = _writable.TryRemove(page.UniqueID, out var dummy);

            ENSURE(removed, "page must be removed from _writable list");

            return readable;
        }

        #endregion

        #region Cache managment

        /// <summary>
        /// Get a clean, re-usable page from store. If store are empty, can extend buffer segments
        /// </summary>
        private PageBuffer GetFreePage()
        {
            if (_free.TryDequeue(out var page))
            {
                ENSURE(page.Position == long.MaxValue, "pages in memory store must have no position defined");
                ENSURE(page.ShareCounter == 0, "pages in memory store must be non-shared");
                ENSURE(page.Origin == FileOrigin.None, "page in memory must have no page origin");

                return page;
            }
            // if no more page inside memory store - extend store/reuse non-shared pages
            else
            {
                this.Extend();

                return this.GetFreePage();
            }
        }

        /// <summary>
        /// Check if pages in cache (read/write) are not been used (ShareCounter = 0) and return to free list
        /// Otherwise, create new pages in memory and add into _free list
        /// </summary>
        private void Extend()
        {
            lock(_locker)
            {
                // count how many pages in cache (read/write) are avaiable to be re-used
                var emptyShareCounter =
                    _readable.Values.Count(x => x.ShareCounter == 0) +
                    _writable.Values.Count(x => x.ShareCounter == 0);

                // if this count are larger than MEMORY_SEGMENT_SIZE, re-use all this pages
                if (emptyShareCounter > MINIMUM_CACHE_REUSE)
                {
                    // get all readable pages that can return to _free
                    var readables = _readable
                        .Where(x => x.Value.ShareCounter == 0)
                        .OrderBy(x => x.Value.Timestamp) // sort by timestamp to re-use oldest pages first
                        .Select(x => x.Key)
                        .Take(MINIMUM_CACHE_REUSE)
                        .ToArray();

                    foreach (var key in readables)
                    {
                        if (_readable.TryRemove(key, out var page))
                        {
                            if (page.ShareCounter != 0)
                            {
                                // if have any change, return page into _readable list
                                if (_readable.TryAdd(key, page)) continue;

                                ENSURE(true, "is possible not re-insert page into _readable list?");
                            }
                            else
                            {
                                // otherwise, make as free buffer
                                page.Position = long.MaxValue;
                                page.Origin = FileOrigin.None;

                                _free.Enqueue(page);
                            }
                        }
                    }

                    // now, do the same with writables pages
                    var writables = _writable
                        .Where(x => x.Value.ShareCounter == 0)
                        .OrderBy(x => x.Value.Timestamp) // sort by timestamp to re-use oldest pages first
                        .Select(x => x.Key)
                        .Take(MINIMUM_CACHE_REUSE)
                        .ToArray();

                    foreach (var key in writables)
                    {
                        if (_writable.TryRemove(key, out var page))
                        {
                            if (page.ShareCounter != 0)
                            {
                                // if have any change, return page into _writable list
                                if (_writable.TryAdd(key, page)) continue;

                                ENSURE(true, "is possible not re-insert page into _writable list?");
                            }

                            // otherwise, make as free buffer
                            page.Position = long.MaxValue;
                            page.Origin = FileOrigin.None;

                            _free.Enqueue(page);
                        }
                    }

                    LOG($"re-using cache pages (flushing {_free.Count} pages)", "CACHE");
                }
                else
                {
                    // create big linear array in heap memory (G2 -> 85Kb)
                    var buffer = new byte[PAGE_SIZE * MEMORY_SEGMENT_SIZE];

                    // slit linear array into many array slices
                    for (var i = 0; i < MEMORY_SEGMENT_SIZE; i++)
                    {
                        var uniqueID = (_extends * MEMORY_SEGMENT_SIZE) + i + 1;

                        _free.Enqueue(new PageBuffer(buffer, i * PAGE_SIZE, uniqueID));
                    }

                    _extends++;

                    LOG($"extending memory usage: (segments: {_extends} - used: {StorageUnitHelper.FormatFileSize(_extends * MEMORY_SEGMENT_SIZE * PAGE_SIZE)})", "CACHE");
                }
            }
        }

        #endregion

        /// <summary>
        /// Return how many pages are in use when call this method (ShareCounter != 0).
        /// Used only for DEBUG propose
        /// </summary>
        public int PagesInUse => 
                _readable.Values.Where(x => x.ShareCounter != 0).Count() +
                _writable.Values.Where(x => x.ShareCounter != 0).Count();

        public void Dispose()
        {
        }
    }
}