using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Duplicati.Library.Main.Database
{
    internal partial class LocalRecreateDatabase : LocalRestoreDatabase
    {
        private class PathEntryKeeper
        {
            private SortedList<KeyValuePair<long, long>, long> m_versions;
                        
            public long GetFilesetID(long blocksetId, long metadataId)
            {
                if (m_versions == null)
                    return -1;

                long r;
                if (!m_versions.TryGetValue(new KeyValuePair<long, long>(blocksetId, metadataId), out r))
                    return -1;
                else
                    return r;
            }
            
            public void AddFilesetID(long blocksetId, long metadataId, long filesetId)
            {
                if (m_versions == null)
                    m_versions = new SortedList<KeyValuePair<long, long>, long>(1, new KeyValueComparer());
                m_versions.Add(new KeyValuePair<long, long>(blocksetId, metadataId), filesetId);
            }
            
            private struct KeyValueComparer : IComparer<KeyValuePair<long, long>>
            {
                public int Compare(KeyValuePair<long, long> x, KeyValuePair<long, long> y)
                {
                    return x.Key == y.Key ? 
                            (x.Value == y.Value ? 
                                0 
                                : (x.Value < y.Value ? -1 : 1)) 
                            : (x.Key < y.Key ? -1 : 1);
                }
            }
        }
        
        private System.Data.IDbCommand m_insertFileCommand;
        private System.Data.IDbCommand m_insertFilesetEntryCommand;
        private System.Data.IDbCommand m_insertMetadatasetCommand;
        private System.Data.IDbCommand m_insertBlocksetCommand;
        private System.Data.IDbCommand m_insertBlocklistEntry;
        private System.Data.IDbCommand m_updateBlockVolumeCommand;
        private System.Data.IDbCommand m_insertBlocklist;
        private System.Data.IDbCommand m_insertBlockSetEntriesFromBlocklists;
        private System.Data.IDbCommand m_insertBlockSetEntriesForSingleBlockset;
        private System.Data.IDbCommand m_insertBlocksetEntry;
        private System.Data.IDbCommand m_findBlocksetCommand;
        private System.Data.IDbCommand m_findBlocksetEntriesCommand;
        private System.Data.IDbCommand m_findBlocklistEntriesCommand;
        private System.Data.IDbCommand m_findMetadatasetCommand;
        private System.Data.IDbCommand m_findFilesetCommand;
        private System.Data.IDbCommand m_findblocklisthashCommand;
        private System.Data.IDbCommand m_findHashBlockCommand;
        private System.Data.IDbCommand m_insertBlockCommand;
        private System.Data.IDbCommand m_insertDuplicateBlockCommand;
        private System.Data.IDbCommand m_truncatePendingBlockCommand;
        private System.Data.IDbCommand m_insertPendingBlockCommand;
        private System.Data.IDbCommand m_updatePendingBlockIdsCommand;
        private System.Data.IDbCommand m_insertNewBlocksFromPendingCommand;
        private System.Data.IDbCommand m_updateBlockSizesFromPendingCommand;
        private System.Data.IDbCommand m_updateVolIdsFromPendingCommand;
        private System.Data.IDbCommand m_insertDuplicateBlocksFromPendingCommand;

        private HashLookupHelper<bool> m_blockListHashLookup;
        private HashLookupHelper<KeyValuePair<long, long>> m_blockHashLookup;
        private HashLookupHelper<long> m_fileHashLookup;
        private HashLookupHelper<long> m_metadataLookup;
        private PathLookupHelper<PathEntryKeeper> m_filesetLookup;

        private string m_tempTabGuid;
        private string m_tempblocklistTab;
        private string m_temppendingblocksTab;
        private string m_sqlSelectPendingIdxWithBlockIdsTemplate;

        
        /// <summary>
        /// A lookup table that prevents multiple downloads of the same volume
        /// </summary>
        private Dictionary<long, long> m_proccessedVolumes;
        
        // SQL that finds index and block size for all blocklist hashes, based on the temporary hash list
        // with vars Used:
        // {0} --> Blocksize
        // {1} --> BlockHash-Size
        // {2} --> Temp-Table
        // {3} --> FullBlocklist-BlockCount [equals ({0} / {1}), if SQLite pays respect to ints]
        private const string SELECT_BLOCKLIST_ENTRIES =
            @" 
        SELECT DISTINCT
            ""E"".""BlocksetID"",
            ""F"".""Index"" + (""E"".""BlocklistIndex"" * {3}) AS ""FullIndex"",
            ""F"".""BlockHash"",
            MIN({0}, ""E"".""Length"" - ((""F"".""Index"" + (""E"".""BlocklistIndex"" * {3})) * {0})) AS ""BlockSize"",
            ""E"".""BlocklistBlockSize"",
            ""E"".""BlocklistBlockHash""
            ""E"".""BlocklistBlockId""
        FROM
            (
                SELECT 
                    ""A"".""BlocksetID"",
                    ""A"".""Index"" AS ""BlocklistIndex"",
                    MIN({3} * {1}, (((""B"".""Length"" + {0} - 1) / {0}) - (""A"".""Index"" * ({3}))) * {1}) AS ""BlocklistBlockSizeCalculated"",
                    ""D"".""ID"" AS ""BlocklistBlockId"",
                    ""D"".""Size"" AS ""BlocklistBlockSize"",
                    ""D"".""Hash"" AS ""BlocklistBlockHash"",
                    ""B"".""Length""
                FROM 
                    ""BlocklistEntry"" A,
                    ""Blockset"" B,
                    ""Block"" D,
                WHERE 
                    ""D"".""ID"" = ""A"".""BlockID""
                    ""B"".""ID"" = ""A"".""BlocksetID""
            ) E,
            ""{2}"" F
        WHERE
           ""F"".""BlocklistBlockHash"" = ""E"".""BlocklistBlockHash""
        ORDER BY 
           ""E"".""BlocksetID"",
           ""FullIndex""
";

        public LocalRecreateDatabase(LocalDatabase parentdb, Options options)
            : base(parentdb)
        {
            m_tempTabGuid = Library.Utility.Utility.ByteArrayAsHexString(Guid.NewGuid().ToByteArray());
            m_tempblocklistTab = "TempBlocklist-" + m_tempTabGuid;
            m_temppendingblocksTab = "TempPendingBlocks-" + m_tempTabGuid;
                        
            using(var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT sqlite_version()";
                var preparserVars = new Dictionary<string, IComparable>(StringComparer.InvariantCultureIgnoreCase) { { "sqlite_version", Version.Parse(cmd.ExecuteScalar().ToString()) } };

                var sqlCreateTempTab =
                @"CREATE TEMPORARY TABLE ""{0}"" " +
                @"     	(  ""BlocklistID"" INTEGER NOT NULL, ""Index"" INTEGER NOT NULL, ""BlockID"" INTEGER NOT NULL, " +
                @"         CONSTRAINT ""{0}_PK_IdIndex"" PRIMARY KEY (""BlocklistID"", ""Index"") " +
                @"      ) {#if sqlite_version >= 3.8.2} WITHOUT ROWID {#endif} ";

                sqlCreateTempTab = Duplicati.Library.SQLiteHelper.DatabaseUpgrader.PreparseSQL(sqlCreateTempTab, preparserVars);
                cmd.ExecuteNonQuery(string.Format(sqlCreateTempTab, m_tempblocklistTab));

                //// Create a second temporary table that is used to temporary store a list of blocks
                //// while it is being resolved.
                sqlCreateTempTab =
                @"CREATE TEMPORARY TABLE ""{0}"" " +
                @"     	(""Idx"" INTEGER PRIMARY KEY, ""Hash"" TEXT, ""Size"" INTEGER, ""BlockId"" INTEGER NULL) ";

                cmd.ExecuteNonQuery(string.Format(sqlCreateTempTab, m_temppendingblocksTab));
            }

            m_insertFileCommand = m_connection.CreateCommand();
            m_insertFilesetEntryCommand = m_connection.CreateCommand();
            m_insertMetadatasetCommand = m_connection.CreateCommand();
            m_insertBlocksetCommand = m_connection.CreateCommand();
            m_insertBlocksetEntry = m_connection.CreateCommand();
            m_insertBlocklistEntry = m_connection.CreateCommand();
            m_updateBlockVolumeCommand = m_connection.CreateCommand();
            m_insertBlocklist = m_connection.CreateCommand();
            m_insertBlockSetEntriesFromBlocklists = m_connection.CreateCommand();
            m_insertBlockSetEntriesForSingleBlockset = m_connection.CreateCommand();
            m_findBlocksetCommand = m_connection.CreateCommand();
            m_findBlocksetEntriesCommand = m_connection.CreateCommand();
            m_findBlocklistEntriesCommand = m_connection.CreateCommand();
            m_findMetadatasetCommand = m_connection.CreateCommand();
            m_findFilesetCommand = m_connection.CreateCommand();
            m_findblocklisthashCommand = m_connection.CreateCommand();
            m_findHashBlockCommand = m_connection.CreateCommand();
            m_insertBlockCommand = m_connection.CreateCommand();
            m_insertDuplicateBlockCommand = m_connection.CreateCommand();
            m_truncatePendingBlockCommand = m_connection.CreateCommand();
            m_insertPendingBlockCommand = m_connection.CreateCommand();
            m_updatePendingBlockIdsCommand = m_connection.CreateCommand();
            m_insertNewBlocksFromPendingCommand = m_connection.CreateCommand();
            m_updateBlockSizesFromPendingCommand = m_connection.CreateCommand();
            m_updateVolIdsFromPendingCommand = m_connection.CreateCommand();
            m_insertDuplicateBlocksFromPendingCommand = m_connection.CreateCommand();

                            
            m_insertFileCommand.CommandText = @"INSERT INTO ""File"" (""Path"", ""BlocksetID"", ""MetadataID"") VALUES (?,?,?); SELECT last_insert_rowid();";
            m_insertFileCommand.AddParameters(3);
            
            m_insertFilesetEntryCommand.CommandText = @"INSERT INTO ""FilesetEntry"" (""FilesetID"", ""FileID"", ""Lastmodified"") VALUES (?,?,?)";
            m_insertFilesetEntryCommand.AddParameters(3);

            m_insertMetadatasetCommand.CommandText = @"INSERT INTO ""MetadataBlockset"" (""BlocksetID"") VALUES (?); SELECT last_insert_rowid();";
            m_insertMetadatasetCommand.AddParameters(1);
            
            m_insertBlocksetCommand.CommandText = @"INSERT INTO ""Blockset"" (""Length"", ""FullHash"") VALUES (?,?); SELECT last_insert_rowid();";
            m_insertBlocksetCommand.AddParameters(2);

            m_insertBlocksetEntry.CommandText = @"INSERT INTO ""BlocksetEntry"" (""BlocksetID"", ""Index"", ""BlockID"") VALUES (?,?,?);";
            m_insertBlocksetEntry.AddParameters(3);

            m_insertBlocklistEntry.CommandText = @"INSERT INTO ""BlocklistEntry"" (""BlocksetID"", ""Index"", ""BlockID"") VALUES (?,?,?);";
            m_insertBlocklistEntry.AddParameters(3);

            m_updateBlockVolumeCommand.CommandText = @"UPDATE ""Block"" SET ""VolumeID"" = ?, ""Size"" = CASE WHEN ""Size"" < 0 THEN ? ELSE ""Size"" END WHERE ""Hash"" = ? AND (""Size"" < 0 OR ""Size"" = ?)";
            m_updateBlockVolumeCommand.AddParameters(5);

            //m_insertBlocklist.CommandText = string.Format(@"INSERT INTO ""{0}"" (""BlocklistID"", ""Index"", ""BlockId"") VALUES (?,?,?) ", m_tempblocklistTab);
            //m_insertBlocklist.AddParameters(3);
            // The ??? mark the spot where the subquery to retrieve data will be inserted.
            m_insertBlocklist.CommandText = string.Format(@"INSERT INTO ""{0}"" (""BlocklistID"", ""Index"", ""BlockId"") SELECT ?, ""Index"", ""BlockId"" FROM (???) ", m_tempblocklistTab);
            m_insertBlocklist.AddParameters(1);
            

            m_insertBlockSetEntriesFromBlocklists.CommandText = string.Format(
                @" INSERT INTO ""BlocksetEntry"" (""BlocksetID"", ""Index"", ""BlockId"") " +
                @" SELECT ""BlocklistEntry"".""BlocksetID"", (""BlocklistEntry"".""Index"" * ?) + ""{0}"".""Index"", ""{0}"".""BlockId"" " +
                @"   FROM ""BlocklistEntry"" INNER JOIN ""{0}"" " +
                @"        ON ""{0}"".""BlocklistID"" = ""BlocklistEntry"".""BlockID"" " +
                @"  WHERE ""{0}"".""BlocklistID"" = ? OR (? = -1)" // @"  WHERE ""BlocklistEntry"".""BlockID"" = ? "
                // Note: This would fail if SQLite actually updates ""BlocksetEntries"" during query execution.
                //       In that case, the condition must either be extended to check against final index (which is on a per row basis).
                //       or the query is modified to get the full BlocklistEntries to update in a subquery.
                + @"    AND NOT EXISTS (SELECT ""BlocksetEntry"".""BlocksetID"" FROM ""BlocksetEntry"" "
                + @"                     WHERE ""BlocksetEntry"".""BlocksetID"" = ""BlocklistEntry"".""BlocksetID"" "
                + @"                       AND ""BlocksetEntry"".""Index"" = (""BlocklistEntry"".""Index"" * ?)) " 
                , m_tempblocklistTab);
            m_insertBlockSetEntriesFromBlocklists.AddParameters(4);

            m_insertBlockSetEntriesForSingleBlockset.CommandText =string.Format(
                @" INSERT INTO ""BlocksetEntry"" (""BlocksetID"", ""Index"", ""BlockId"") " +
                @" SELECT ""BlocklistEntry"".""BlocksetID"", (""BlocklistEntry"".""Index"" * ?) + ""{0}"".""Index"", ""{0}"".""BlockId"" " +
                @"   FROM ""BlocklistEntry"" INNER JOIN ""{0}"" " +
                @"        ON ""{0}"".""BlocklistID"" = ""BlocklistEntry"".""BlockID"" " +
                @"  WHERE  ""BlocklistEntry"".""BlocksetID"" = ? "
                , m_tempblocklistTab);
            m_insertBlockSetEntriesForSingleBlockset.AddParameters(2); // HashesPerBlock, BlocksetId 

            m_findBlocksetCommand.CommandText = @"SELECT ""ID"" FROM ""Blockset"" WHERE ""Length"" = ? AND ""FullHash"" = ? ";
            m_findBlocksetCommand.AddParameters(2);

            m_findBlocksetEntriesCommand.CommandText = @"SELECT IFNULL(Count(""Index""), 0) FROM ""BlocksetEntry"" WHERE ""BlocksetID"" = ? ";
            m_findBlocksetEntriesCommand.AddParameters(1);

            m_findBlocklistEntriesCommand.CommandText = @"SELECT IFNULL(Count(""Index""), 0) FROM ""BlocklistEntry"" WHERE ""BlocksetID"" = ? ";
            m_findBlocklistEntriesCommand.AddParameters(1);

            m_findMetadatasetCommand.CommandText = @"SELECT ""MetadataBlockset"".""BlocksetID"" FROM ""MetadataBlockset"",""Blockset"" WHERE ""MetadataBlockset"".""BlocksetID"" = ""Blockset"".""ID"" AND ""Blockset"".""FullHash"" = ? AND ""Blockset"".""Length"" = ? ";
            m_findMetadatasetCommand.AddParameters(2);
            
            m_findFilesetCommand.CommandText = @"SELECT ""ID"" FROM ""File"" WHERE ""Path"" = ? AND ""BlocksetID"" = ? AND ""MetadataID"" = ? ";
            m_findFilesetCommand.AddParameters(3);

            m_findblocklisthashCommand.CommandText = string.Format(@"SELECT ""BlocklistID"" FROM ""{0}"" WHERE ""BlocklistID"" = ? LIMIT 1", m_tempblocklistTab);
            m_findblocklisthashCommand.AddParameters(1);

            m_findHashBlockCommand.CommandText = @"SELECT ""ID"", ""VolumeID"" FROM ""Block"" WHERE ""Hash"" = ? AND (""Size"" = ? OR ? < 0 OR ""Size"" < 0)";
            m_findHashBlockCommand.AddParameters(3);

            m_insertBlockCommand.CommandText = @"INSERT INTO ""Block"" (""Hash"", ""Size"", ""VolumeID"") VALUES (?,?,?); SELECT last_insert_rowid();";
            m_insertBlockCommand.AddParameters(3);
            
            m_insertDuplicateBlockCommand.CommandText = @"INSERT INTO ""DuplicateBlock"" (""BlockID"", ""VolumeID"") VALUES ((SELECT ""ID"" FROM ""Block"" WHERE ""Hash"" = ? AND ""Size"" = ?), ?)";
            m_insertDuplicateBlockCommand.AddParameters(3);

            m_truncatePendingBlockCommand.CommandText = string.Format(@"DELETE FROM ""{0}"" ", m_temppendingblocksTab);

            m_insertPendingBlockCommand.CommandText = string.Format(@"INSERT INTO ""{0}"" (""Idx"", ""Hash"", ""Size"") VALUES (?,?,?) ", m_temppendingblocksTab);
            m_insertPendingBlockCommand.AddParameters(3);

            m_updatePendingBlockIdsCommand.CommandText =  string.Format(@" UPDATE ""{0}"" SET ""BlockId"" = " +
                @" (SELECT ""ID"" FROM ""Block"" WHERE ""Block"".""Hash""=""{0}"".""Hash"" AND (""Block"".""Size""=""{0}"".""Size"" OR ""Block"".""Size"" < 0 OR ""{0}"".""Size"" < 0))" , m_temppendingblocksTab);

            // Note: The DISTINCT is important if there are blocks registered multiplely in a vol or blocks are multiplely used in one blocklist
            m_insertNewBlocksFromPendingCommand.CommandText =
                string.Format(@" INSERT INTO ""Block"" (""Hash"", ""Size"", ""VolumeID"") " +
                              @" SELECT DISTINCT ""Hash"", ""Size"", ? FROM ""{0}"" WHERE ""BlockId"" IS NULL ", m_temppendingblocksTab);
            m_insertNewBlocksFromPendingCommand.AddParameters(1);

            m_updateBlockSizesFromPendingCommand.CommandText =
                string.Format(@"UPDATE ""Block"" SET ""Size"" = (SELECT ""Size"" FROM ""{0}"" WHERE ""BlockId"" = ""Block"".""ID"") " +
                              @" WHERE ""Block"".""ID"" IN (SELECT ""BlockId"" FROM ""{0}"" WHERE (NOT ""BlockId"" IS NULL) AND ""{0}"".Size >= 0) " +
                              @"   AND ""Block"".""Size"" < 0  AND ""Block"".""VolumeID"" < 0 ", m_temppendingblocksTab);
            m_updateBlockSizesFromPendingCommand.AddParameters(2);

            m_updateVolIdsFromPendingCommand.CommandText =
                string.Format(@"UPDATE ""Block"" SET ""VolumeID"" = ? " +
                              @" WHERE ""Block"".""ID"" IN (SELECT ""BlockId"" FROM ""{0}"" WHERE NOT ""BlockId"" IS NULL) " +
                              @"   AND ""Block"".""VolumeID"" < 0  AND ""Block"".""VolumeID"" < ? ", m_temppendingblocksTab);
            m_updateVolIdsFromPendingCommand.AddParameters(2);

            m_insertDuplicateBlocksFromPendingCommand.CommandText =
                string.Format(@"INSERT INTO ""DuplicateBlock"" (""BlockID"", ""VolumeID"") " +
                              @" SELECT DISTINCT ""BlockId"", ? FROM ""{0}"", ""Block"" " +
                              @"  WHERE ""{0}"".""BlockId"" = ""Block"".""ID"" " +
                              @"    AND NOT (""Block"".""VolumeID"" = ?) ", m_temppendingblocksTab);
            m_insertDuplicateBlocksFromPendingCommand.AddParameters(2);

            m_sqlSelectPendingIdxWithBlockIdsTemplate = 
                string.Format(@"SELECT ""Idx"" AS ""Index"", IFNULL(""Block"".""ID"", ""{0}"".""BlockId"") as ""BlockId"" " +
                              @"  FROM ""{0}"" LEFT JOIN ""Block"" ON (""{0}"".""BlockId"" IS NULL AND ""Block"".""Hash""=""{0}"".""Hash"" AND ""Block"".""Size""=""{0}"".""Size"")" , m_temppendingblocksTab);



            if (options.BlockHashLookupMemory > 0)
            {
                m_blockHashLookup = new HashLookupHelper<KeyValuePair<long, long>>((ulong)options.BlockHashLookupMemory / 2);
                m_blockListHashLookup = new HashLookupHelper<bool>((ulong)options.BlockHashLookupMemory/2);
            }
            if (options.FileHashLookupMemory > 0)
                m_fileHashLookup = new HashLookupHelper<long>((ulong)options.FileHashLookupMemory);
            if (options.MetadataHashMemory > 0)
                m_metadataLookup = new HashLookupHelper<long>((ulong)options.MetadataHashMemory);
            if (options.UseFilepathCache)
                m_filesetLookup = new PathLookupHelper<PathEntryKeeper>();
        }

        public void InsertMissingSingleBlockEntries(long blocksize, System.Data.IDbTransaction transaction)
        {
            var sql = @" INSERT INTO ""BlocksetEntry"" (""BlocksetId"", ""Index"", ""BlockId"") "
                    + @" SELECT ""Blockset"".""ID"", 0, ""Block"".""ID"" "
                    + @"   FROM ""Blockset"", ""Block"" "
                    + @"  WHERE ""Blockset"".""FullHash"" = ""Block"".""Hash"" "
                    + @"    AND ""Blockset"".""Length"" = ""Block"".""Size"" "
                    + @"    AND ""Blockset"".""Length"" <= {0} "
                    + @"    AND NOT EXISTS (SELECT ""L"".""BlocksetId"" FROM ""BlocksetEntry"" ""L"" WHERE ""L"".""BlocksetId"" = ""Blockset"".""ID"")"
                    ;
            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = string.Format(sql, blocksize);
                cmd.Transaction = transaction;
                int rows = cmd.ExecuteNonQuery();
            }
        }

        //public void FindMissingBlocklistHashes(long hashsize, long blocksize, System.Data.IDbTransaction transaction)
        //{
        //    using(var cmd = m_connection.CreateCommand())
        //    {
        //        cmd.Transaction = transaction;
                
        //        //Update all small blocklists and matching blocks
        //        var selectSmallBlocks = string.Format(@"SELECT ""Blockset"".""Fullhash"", ""Blockset"".""Length"" FROM ""Blockset"" WHERE ""Blockset"".""Length"" <= {0}", blocksize);
            
        //        var selectBlockHashes = string.Format(
        //            @"SELECT ""BlockHash"" AS ""FullHash"", ""BlockSize"" AS ""Length"" FROM ( " +
        //            SELECT_BLOCKLIST_ENTRIES +
        //            @" )",
        //            blocksize,
        //            hashsize,
        //            m_tempblocklist,
        //            blocksize / hashsize
        //        );
                                
        //        var selectAllBlocks = @"SELECT DISTINCT ""FullHash"", ""Length"" FROM (" + selectBlockHashes + " UNION " + selectSmallBlocks + " )";
                
        //        var selectNewBlocks = string.Format(
        //            @"SELECT ""FullHash"" AS ""Hash"", ""Length"" AS ""Size"", -1 AS ""VolumeID"" " +
        //            @" FROM (SELECT ""A"".""FullHash"", ""A"".""Length"", CASE WHEN ""B"".""Hash"" IS NULL THEN '' ELSE ""B"".""Hash"" END AS ""Hash"", CASE WHEN ""B"".""Size"" is NULL THEN -1 ELSE ""B"".""Size"" END AS ""Size"" FROM ({0}) A" + 
        //            @" LEFT OUTER JOIN ""Block"" B ON ""B"".""Hash"" =  ""A"".""FullHash"" AND ""B"".""Size"" = ""A"".""Length"" )" + 
        //            @" WHERE ""FullHash"" != ""Hash"" AND ""Length"" != ""Size"" ",
        //            selectAllBlocks    
        //        );
                
        //        var insertBlocksCommand = 
        //            @"INSERT INTO ""Block"" (""Hash"", ""Size"", ""VolumeID"") " + 
        //            selectNewBlocks;
                    
        //        // Insert all known blocks into block table with volumeid = -1
        //        var blocksInserted = cmd.ExecuteNonQuery(insertBlocksCommand);
                    
        //        // Update the cache with new blocks
        //        if (m_blockHashLookup != null)
        //        {
        //            using(var rd = cmd.ExecuteReader(@"SELECT ""ID"", ""Hash"", ""Size"" FROM ""Block"" WHERE ""VolumeID"" = -1 "))
        //                while(rd.Read())
        //                {
        //                    var id = rd.GetInt64(0);
        //                    var hash = rd.GetString(1);
        //                    var size = rd.GetInt64(2);
        //                    m_blockHashLookup[hash, size] = new KeyValuePair<long, long>(id, -1);
        //                }
        //        }

        //        var selectBlocklistBlocksetEntries = string.Format(
        //            @"SELECT ""D"".""BlocksetID"" AS ""BlocksetID"", ""D"".""FullIndex"" AS ""Index"", ""F"".""ID"" AS ""BlockID"" FROM ( " +
        //            SELECT_BLOCKLIST_ENTRIES +
        //            @") D, ""Block"" F WHERE ""D"".""Blockhash"" = ""F"".""Hash"" AND ""D"".""BlockSize"" = ""F"".""Size"" ",
        //            blocksize,
        //            hashsize,
        //            m_tempblocklist,
        //            blocksize / hashsize
        //            );
                    
        //        var selectBlocksetEntries = string.Format(
        //            @"SELECT ""Blockset"".""ID"" AS ""BlocksetID"", 0 AS ""Index"", ""Block"".""ID"" AS ""BlockID"" FROM ""Blockset"", ""Block"" WHERE ""Blockset"".""Fullhash"" = ""Block"".""Hash"" AND ""Blockset"".""Length"" <= {0} ",
        //            blocksize
        //            );
                    
        //        var selectAllBlocksetEntries =
        //            selectBlocklistBlocksetEntries +
        //            @" UNION " +
        //            selectBlocksetEntries;
                    
        //        var selectFiltered =
        //            @"SELECT DISTINCT ""BlocksetID"", ""Index"", ""BlockID"" FROM (" +
        //            selectAllBlocksetEntries +
        //            @") A WHERE (""A"".""BlocksetID"" || ':' || ""A"".""Index"") NOT IN (SELECT (""BlocksetID"" || ':' || ""Index"") FROM ""BlocksetEntry"" )";
                
        //        var insertBlocksetEntriesCommand =
        //            @"INSERT INTO ""BlocksetEntry"" (""BlocksetID"", ""Index"", ""BlockID"") " + selectFiltered;

        //        var blocksetEntriesInserted = cmd.ExecuteNonQuery(insertBlocksetEntriesCommand);                
        //    }
        //}
        
        public void AddDirectoryEntry(long filesetid, string path, DateTime time, string metahash, long metahashsize, System.Data.IDbTransaction transaction)
        {
            AddEntry(FilelistEntryType.Folder, filesetid, path, time, FOLDER_BLOCKSET_ID, metahash, metahashsize, transaction);
        }

        public void AddSymlinkEntry(long filesetid, string path, DateTime time, string metahash, long metahashsize, System.Data.IDbTransaction transaction)
        {
            AddEntry(FilelistEntryType.Symlink, filesetid, path, time, SYMLINK_BLOCKSET_ID, metahash, metahashsize, transaction);
        }
        
        public void AddFileEntry(long filesetid, string path, DateTime time, long blocksetid, string metahash, long metahashsize, System.Data.IDbTransaction transaction)
        {
            AddEntry(FilelistEntryType.File , filesetid, path, time, blocksetid, metahash, metahashsize, transaction);
        }
        
        private void AddEntry(FilelistEntryType type, long filesetid, string path, DateTime time, long blocksetid, string metahash, long metahashsize, System.Data.IDbTransaction transaction)
        {
            var fileid = -1L;
            var metadataid = AddMetadataset(metahash, metahashsize, transaction);
                        
            if (m_filesetLookup != null)
            {
                PathEntryKeeper e;
                if (m_filesetLookup.TryFind(path, out e))
                    fileid = e.GetFilesetID(blocksetid, metadataid);
            }
            else
            {
                m_findFilesetCommand.Transaction = transaction;
                m_findFilesetCommand.SetParameterValue(0, path);
                m_findFilesetCommand.SetParameterValue(1, blocksetid);
                m_findFilesetCommand.SetParameterValue(2, metadataid);
                fileid = m_findFilesetCommand.ExecuteScalarInt64(-1);
            }
            
            if (fileid < 0)
            {
                m_insertFileCommand.Transaction = transaction;
                m_insertFileCommand.SetParameterValue(0, path);
                m_insertFileCommand.SetParameterValue(1, blocksetid);
                m_insertFileCommand.SetParameterValue(2, metadataid);
                fileid = m_insertFileCommand.ExecuteScalarInt64(-1);
                if (m_filesetLookup != null)
                {
                    PathEntryKeeper e;
                    if (m_filesetLookup.TryFind(path, out e))
                        e.AddFilesetID(blocksetid, metadataid, fileid);
                    else
                    {
                        e = new PathEntryKeeper();
                        e.AddFilesetID(blocksetid, metadataid, fileid);
                        m_filesetLookup.Insert(path, e);
                    }
                }
            }
            
            m_insertFilesetEntryCommand.Transaction = transaction;
            m_insertFilesetEntryCommand.SetParameterValue(0, filesetid);
            m_insertFilesetEntryCommand.SetParameterValue(1, fileid);
            m_insertFilesetEntryCommand.SetParameterValue(2, time.ToUniversalTime().Ticks);
            m_insertFilesetEntryCommand.ExecuteNonQuery();
        }
        
        /// <summary>
        /// Find or insert a (empty) blockset for the specified (metahash, metahashsize) tuple.
        /// </summary>
        public long AddMetadataset(string metahash, long metahashsize, System.Data.IDbTransaction transaction)
        {
            var metadataid = -1L;
            if (metahash == null)
                return metadataid;
                                
            if (m_metadataLookup != null)
            {
                if (m_metadataLookup.TryGet(metahash, metahashsize, out metadataid))
                    return metadataid;
                else
                    metadataid = -1;
            }
            else
            {
                m_findMetadatasetCommand.Transaction = transaction;
                m_findMetadatasetCommand.SetParameterValue(0, metahash);
                m_findMetadatasetCommand.SetParameterValue(1, metahashsize);
                metadataid = m_findMetadatasetCommand.ExecuteScalarInt64(-1);
                if (metadataid != -1)
                    return metadataid;
            }
            
            // Blockset only good to store hash and size (dummy entry, but sometimes enough for single block metadata).
            // If there is specific block/blocklist definition coming, this data might be recorded later.
            var blocksetid = AddEmptyBlockset(metahash, metahashsize, transaction);
            
            m_insertMetadatasetCommand.Transaction = transaction;
            m_insertMetadatasetCommand.SetParameterValue(0, blocksetid);
            metadataid = m_insertMetadatasetCommand.ExecuteScalarInt64(-1);
            
            if (m_metadataLookup != null)
                m_metadataLookup.Add(metahash, metahashsize, metadataid);
                
            return metadataid;
        }

        public bool AddBlocksetEntryDefs(long blocksetid, IEnumerable<string> blocklisthashes, long expectedblocklisthashes, IEnumerable<long> expectedblocklistsizes, bool hashesAreBlocks, int hashesPerBlock, System.Data.IDbTransaction transaction)
        {
            // It is allowed to record blocksets (for id) before they are actually defined with
            // their blocks / blocklists.

            // We have to allow to override blocksets, if metadata is recorded and later specified.
            // Otherwise, we can create "EMPTY" blocksets. That might be useful to fill it up later.
            // Maybe separate meta and blockset (no auto-lookup)??

            // Check if a blocklist definition already exists in db (inserted before)

            m_findBlocksetEntriesCommand.SetParameterValue(0, blocksetid);
            long blockSetEntries = m_findBlocksetEntriesCommand.ExecuteScalarInt64(0);
            m_findBlocklistEntriesCommand.SetParameterValue(0, blocksetid);
            long blockListEntries = m_findBlocklistEntriesCommand.ExecuteScalarInt64(0);

            // we could actually use the counts returned to check against expectedblocklisthashes
            // ->  it should either match blockentries (hashesAreBlocks) or blocklistentries.
            // But there is no real need to bother with it except for debugging reasons.

            if (!(blockListEntries > 0 || blockSetEntries > 0))
            {
                var cmdInsertBlockEntry = (hashesAreBlocks)
                    ? m_insertBlocksetEntry // blocklist hashes actually is just a direct list of blocks 
                    : m_insertBlocklistEntry; // default, really blocklisthashes

                var index = 0L;
                cmdInsertBlockEntry.Transaction = transaction;
                cmdInsertBlockEntry.SetParameterValue(0, blocksetid);

                long c = 0;
                using (IEnumerator<long> expectedsizesEnum = (expectedblocklistsizes == null) ? Enumerable.Repeat<long>(-1, (int)expectedblocklisthashes).GetEnumerator() : expectedblocklistsizes.GetEnumerator())
                {
                    foreach (var hash in blocklisthashes)
                    {
                        if (!string.IsNullOrEmpty(hash))
                        {
                            c++;
                            if (c <= expectedblocklisthashes)
                            {
                                long nextsize = expectedsizesEnum.MoveNext() ? expectedsizesEnum.Current : -1;
                                long blockId = UpdateOrRegisterBlock(hash, nextsize, -1, transaction);
                                cmdInsertBlockEntry.SetParameterValue(1, index++);
                                cmdInsertBlockEntry.SetParameterValue(2, blockId);
                                cmdInsertBlockEntry.ExecuteNonQuery();
                            }
                        }
                    }
                }

                if (c != expectedblocklisthashes) // or is there a legacy with single element blocklist hashes? --> && !(c == 1 && hash == computeBlockHash(fullhash))
                    m_result.AddWarning(string.Format("Mismatching number of blocklist hashes detected on blockset {2}. Expected {0} blocklist hashes, but found {1}", expectedblocklisthashes, c, blocksetid), null);

                //// As we now already have the logic to directly insert block entries here anyway, we can also insert for "normal" single block files
                //// These are files where the filehash equals the blockhash as the hashing algos are the same.
                //if (expectedblocklisthashes == 0 && c == 0)
                //{
                //    long blockId = UpdateOrRegisterBlock(fullhash, size, -1, transaction);
                //    m_insertBlocksetEntry.SetParameterValue(1, 0);
                //    m_insertBlocksetEntry.SetParameterValue(2, blockId);
                //    m_insertBlocksetEntry.ExecuteNonQuery();
                //}

                if (c != expectedblocklisthashes) // or is there a legacy with single element blocklist hashes? --> && !(c == 1 && hash == computeBlockHash(fullhash))
                    m_result.AddWarning(string.Format("Mismatching number of blocklist hashes detected on blockset {2}. Expected {0} blocklist hashes, but found {1}", expectedblocklisthashes, c, blocksetid), null);

                if (!hashesAreBlocks && c > 0) // create blockset entries for known blocklists.
                {
                    m_insertBlockSetEntriesForSingleBlockset.SetParameterValue(0, hashesPerBlock);
                    m_insertBlockSetEntriesForSingleBlockset.SetParameterValue(1, blocksetid);
                    m_insertBlockSetEntriesForSingleBlockset.ExecuteNonQuery();
                }
                return true;
            }
            else
                return false;
        }

        public long AddEmptyBlockset(string fullhash, long size, System.Data.IDbTransaction transaction)
        {
            var blocksetid = -1L;

            if (m_fileHashLookup != null)
            {
                if (!m_fileHashLookup.TryGet(fullhash, size, out blocksetid))
                    blocksetid = -1;
            }
            else
            {
                m_findBlocksetCommand.Transaction = transaction;
                m_findBlocksetCommand.SetParameterValue(0, size);
                m_findBlocksetCommand.SetParameterValue(1, fullhash);
                blocksetid = m_findBlocksetCommand.ExecuteScalarInt64(-1);
            }

            if (blocksetid < 0)
            {
                m_insertBlocksetCommand.Transaction = transaction;
                m_insertBlocksetCommand.SetParameterValue(0, size);
                m_insertBlocksetCommand.SetParameterValue(1, fullhash);
                blocksetid = m_insertBlocksetCommand.ExecuteScalarInt64();

                if (m_fileHashLookup != null)
                    m_fileHashLookup.Add(fullhash, size, blocksetid);
            }

            return blocksetid;
        }



        /// <summary>
        /// Registers new or updates existing blocks specified by (hash,size) with the passed volumeID if it is valid (positive).
        /// The operation should equal just adding all blocks by itself, but is faster using a temporary table and 
        /// thus processing all blocks at once.
        /// It is used to process all blocks from a volume / all blocks from a blockset.
        /// </summary>
        public long UpdateOrRegisterBlocksInVolume(IEnumerable<KeyValuePair<string, long>> blocks, long volumeID, System.Data.IDbCommand sqlStoreIndexedIds, System.Data.IDbTransaction transaction)
        {
            using (var cmd = m_connection.CreateCommand())
            {
                m_truncatePendingBlockCommand.Transaction = transaction;
                m_truncatePendingBlockCommand.ExecuteNonQuery();

                int blockCount = 0;
                bool hasSizes = false;
                m_insertPendingBlockCommand.Transaction = transaction;
                foreach (var b in blocks)
                {
                    hasSizes |= (b.Value >= 0);
                    m_insertPendingBlockCommand.SetParameterValue(0, blockCount++);
                    m_insertPendingBlockCommand.SetParameterValue(1, b.Key);
                    m_insertPendingBlockCommand.SetParameterValue(2, b.Value);
                    m_insertPendingBlockCommand.ExecuteNonQuery();
                }

                int newCount = 0, updSizeCount = 0, updVolCount = 0;

                // Find out which blocks are already registered (have BlockId, which is updater in Pending table)
                m_updatePendingBlockIdsCommand.Transaction = transaction;
                m_updatePendingBlockIdsCommand.ExecuteNonQuery(); // technically (returned rowcount) updates all, not just existing blocks

                // Insert distinct list of unknown blocks from pending list as new blocks into table "Block"
                m_insertNewBlocksFromPendingCommand.Transaction = transaction;
                m_insertNewBlocksFromPendingCommand.SetParameterValue(0, volumeID);
                newCount = m_insertNewBlocksFromPendingCommand.ExecuteNonQuery();

                // If not all blocks where new, we have to care about updating the infos in table Block
                if (newCount < blockCount)
                {
                    // Update sizes of blocks if previously recorded without
                    if (hasSizes) // an update of the size only makes sense if there are sizes provided in the list (which is not the case for blocklists)
                    {
                        m_updateBlockSizesFromPendingCommand.Transaction = transaction;
                        m_updateBlockSizesFromPendingCommand.SetParameterValue(0, volumeID);
                        m_updateBlockSizesFromPendingCommand.SetParameterValue(1, volumeID);
                        updSizeCount = m_updateBlockSizesFromPendingCommand.ExecuteNonQuery();
                    }

                    // Update VolumeId of blocks if previously recorded without
                    m_updateVolIdsFromPendingCommand.Transaction = transaction;
                    m_updateVolIdsFromPendingCommand.SetParameterValue(0, volumeID);
                    m_updateVolIdsFromPendingCommand.SetParameterValue(1, volumeID);
                    updVolCount = m_updateVolIdsFromPendingCommand.ExecuteNonQuery();

                    // there is a theoretical chance that a duplicate block exists. Record it.
                    if (updVolCount + newCount < blockCount && volumeID >= 0)
                    {
                        m_insertDuplicateBlocksFromPendingCommand.Transaction = transaction;
                        m_insertDuplicateBlocksFromPendingCommand.SetParameterValue(0, volumeID);
                        m_insertDuplicateBlocksFromPendingCommand.SetParameterValue(1, volumeID);
                        updVolCount = m_insertDuplicateBlocksFromPendingCommand.ExecuteNonQuery();
                    }
                }

                // Finally, we offer to store the processed blocks with their Id's via an externally
                // supplied sql command. This is used to temporary store resolved blocklists.
                if (sqlStoreIndexedIds != null)
                {
                    string sqlTemplateSave = sqlStoreIndexedIds.CommandText;
                    try
                    {
                        sqlStoreIndexedIds.CommandText = sqlTemplateSave.Replace("???", m_sqlSelectPendingIdxWithBlockIdsTemplate);
                        sqlStoreIndexedIds.Transaction = transaction;
                        int insCnt = sqlStoreIndexedIds.ExecuteNonQuery();
                    }
                    finally { sqlStoreIndexedIds.CommandText = sqlTemplateSave; }
                }

                return newCount;

            }
        }

        /// <summary>
        /// Registers a new or updates an existing block specified by (hash,size) with the passed volumeID if it is valid (positive).
        /// If a block already has positive volumeID, another positive volumeId is recorded in DuplicateBlock.
        /// If a negative volumeID is passed, an existing volumeId is only overriden if it was "more" negative before.
        /// Returns the new or existing BlockId for the block identified by (hash,size).
        /// If the volumeId to set is long.MinValue, the block will not be added if it was not present before.
        /// </summary>
        public long UpdateOrRegisterBlock(string hash, long size, long volumeID, System.Data.IDbTransaction transaction)
        {
            long dummy;
            return UpdateOrRegisterBlock(hash, size, volumeID, transaction, out dummy);
        }

        /// <summary>
        /// Registers a new or updates an existing block specified by (hash,size) with the passed volumeID if it is valid (positive).
        /// If a block already has positive volumeID, another positive volumeId is recorded in DuplicateBlock.
        /// If a negative volumeID is passed, an existing volumeId is only overriden if it was "more" negative before.
        /// Returns the new or existing BlockId for the block identified by (hash,size).
        /// prevVolumeId will be long.MinValue if no block was present before, otherwise the current value found in DB.
        /// If the volumeId to set is long.MinValue, the block will not be added if it was not present before.
        /// </summary>
        public long UpdateOrRegisterBlock(string hash, long size, long volumeID, System.Data.IDbTransaction transaction, out long prevVolumeId)
        {
            var blockWithVolId = new KeyValuePair<long, long>(-1, long.MinValue);
            if (m_blockHashLookup != null)
            {
                if (!m_blockHashLookup.TryGet(hash, size, out blockWithVolId))
                    blockWithVolId = new KeyValuePair<long, long>(-1, long.MinValue);
            }
            else
            {
                m_findHashBlockCommand.Transaction = transaction;
                m_findHashBlockCommand.SetParameterValue(0, hash);
                m_findHashBlockCommand.SetParameterValue(1, size);
                m_findHashBlockCommand.SetParameterValue(2, size);
                var block = m_findHashBlockCommand.ExecuteScalarKeyValue(-1);
                blockWithVolId = (block.Key == -1) ? new KeyValuePair<long, long>(-1, long.MinValue)
                    : new KeyValuePair<long, long>(block.Key, Convert.ToInt64(block.Value));
            }

            prevVolumeId = blockWithVolId.Value;

            if (prevVolumeId == volumeID)
                return blockWithVolId.Key;

            if (blockWithVolId.Key == -1) // New block
            {
                m_insertBlockCommand.Transaction = transaction;
                m_insertBlockCommand.SetParameterValue(0, hash);
                m_insertBlockCommand.SetParameterValue(1, size);
                m_insertBlockCommand.SetParameterValue(2, volumeID);
                var newBlockId = m_insertBlockCommand.ExecuteScalarInt64();

                if (m_blockHashLookup != null)
                    m_blockHashLookup[hash, size] = new KeyValuePair<long,long>(newBlockId, volumeID);

                return newBlockId;
            }
            else if (    (volumeID <  0 && volumeID > prevVolumeId) // Update VolId
                      || (volumeID >= 0 && prevVolumeId < 0))
            {
                m_updateBlockVolumeCommand.Transaction = transaction;
                m_updateBlockVolumeCommand.SetParameterValue(0, volumeID);
                m_updateBlockVolumeCommand.SetParameterValue(1, size);
                m_updateBlockVolumeCommand.SetParameterValue(2, hash);
                m_updateBlockVolumeCommand.SetParameterValue(3, size);
                m_updateBlockVolumeCommand.SetParameterValue(4, size);
                var c = m_updateBlockVolumeCommand.ExecuteNonQuery();
                if (c != 1)
                    throw new Exception(string.Format("Failed to update table, found {0} entries for key {1} with size {2}", c, hash, size));

                if (m_blockHashLookup != null)
                    m_blockHashLookup[hash, size] = new KeyValuePair<long, long>(blockWithVolId.Key, volumeID);
            }
            else if (volumeID >= 0 && prevVolumeId >= 0) // Record duplicate VolId
            {
                m_insertDuplicateBlockCommand.Transaction = transaction;
                m_insertDuplicateBlockCommand.SetParameterValue(0, hash);
                m_insertDuplicateBlockCommand.SetParameterValue(1, size);
                m_insertDuplicateBlockCommand.SetParameterValue(2, volumeID);
                m_insertDuplicateBlockCommand.ExecuteNonQuery();
            }
            return blockWithVolId.Key;
        }


        /// <summary> Inserts BlocksetEntries for all blocksets where blocklist blocklistID is used. Returns if it was used at all. </summary>
        public bool UpdateBlocksetsWithBlocklistDef(long blocklistID, int hashesPerBlock, System.Data.IDbTransaction transaction)
        {
            m_insertBlockSetEntriesFromBlocklists.SetParameterValue(0, hashesPerBlock);
            m_insertBlockSetEntriesFromBlocklists.SetParameterValue(1, blocklistID);
            m_insertBlockSetEntriesFromBlocklists.SetParameterValue(2, blocklistID);
            m_insertBlockSetEntriesFromBlocklists.SetParameterValue(3, hashesPerBlock);
            
            return (m_insertBlockSetEntriesFromBlocklists.ExecuteNonQuery() > 0);
        }

        /// <summary> Records a blockset definition in a temporary table. Returns whether it was new. </summary>
        public bool UpdateBlocklist(long blocklistID, IEnumerable<string> blocklisthashes, System.Data.IDbTransaction transaction)
        {
            m_findblocklisthashCommand.Transaction = transaction;
            m_findblocklisthashCommand.SetParameterValue(0, blocklistID);
            var r = m_findblocklisthashCommand.ExecuteScalar();
            if (r != null && r != DBNull.Value)
                return false;
            
            m_insertBlocklist.Transaction = transaction;
            m_insertBlocklist.SetParameterValue(0, blocklistID);
            long newBlocks = UpdateOrRegisterBlocksInVolume(blocklisthashes.Select(h => new KeyValuePair<string, long>(h, -1)), -2, m_insertBlocklist, transaction);

            //var index = 0L;
            //foreach(var s in blocklisthashes)
            //{
            //    long blockId = UpdateOrRegisterBlock(s, -1, -2, transaction);
            //    m_insertBlocklist.SetParameterValue(1, index++);
            //    m_insertBlocklist.SetParameterValue(2, blockId);
            //    m_insertBlocklist.ExecuteNonQuery();
            //}

            return true;
        }            


        public IEnumerable<Tuple<long, string, long>> GetBlockListBlocks(long volumeid)
        {
            using(var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = string.Format(
                    @"SELECT ""Block"".""ID"", ""Block"".""Hash"", ""Block"".""Size""  FROM ""Block"" " +
                    @" WHERE ""Block"".""VolumeID"" = ?  AND EXISTS (SELECT ""BlocklistEntry"".""BlockId"" FROM ""BlocklistEntry"" WHERE ""BlocklistEntry"".""BlockId""=""Block"".""ID"") ");
                cmd.AddParameter(volumeid);
                
                using(var rd = cmd.ExecuteReader())
                    while (rd.Read())
                        yield return new Tuple<long, string, long>(rd.ConvertValueToInt64(0), rd.GetValue(1).ToString(), rd.ConvertValueToInt64(2));
            }
        }

        public IEnumerable<IRemoteVolume> GetMissingBlockListVolumes(int passNo, int hashsperblocklist)
        {
            using(var cmd = m_connection.CreateCommand())
            {
                var selectCommand = @"SELECT DISTINCT ""RemoteVolume"".""Name"", ""RemoteVolume"".""Hash"", ""RemoteVolume"".""Size"", ""RemoteVolume"".""ID"" FROM ""RemoteVolume""";

                //! Test which SQL is faster
                // Alternative with join:
                //var missingBlocklistEntries = String.Format(
                //    @"SELECT ""BlocklistEntry"".""BlockId"" " +
                //    @"  FROM ""BlocklistEntry"" " +
                //    @"        LEFT OUTER JOIN ""BlocksetEntry"" " +
                //    @"          ON ""BlocksetEntry"".""BlocksetID"" = ""BlocklistEntry"".""BlocksetID"" " +
                //    @"         AND ""BlocksetEntry"".""Index"" = (""BlocklistEntry"".""Index"" * {0}) " +
                //    @" WHERE ""BlocksetEntry"".""BlocksetID"" IS NULL", hashsperblocklist); ;

                var missingBlocklistEntries = String.Format(
                    @"SELECT ""BlocklistEntry"".""BlockId"" " +
                    @"  FROM ""BlocklistEntry"" " +
                    @" WHERE NOT EXISTS (SELECT ""BlocksetEntry"".""BlocksetID"" " +
                    @"                     FROM ""BlocksetEntry"" " +
                    @"                    WHERE ""BlocksetEntry"".""BlocksetID"" = ""BlocklistEntry"".""BlocksetID"" " +
                    @"                      AND ""BlocksetEntry"".""Index"" = (""BlocklistEntry"".""Index"" * {0}) ) " 
                       , hashsperblocklist);

                var missingBlockInfo = 
                    @"SELECT ""VolumeID"" FROM ""Block"" WHERE ""VolumeID"" < 0 ";
            
                var missingBlocklistVolumes = string.Format(
                    @"SELECT ""VolumeID"" FROM ""Block"", (" +
                    missingBlocklistEntries + 
                    @") A WHERE ""A"".""BlockId"" = ""Block"".""ID"" "
                );
                
                var countMissingInformation = string.Format(
                    @"SELECT COUNT(*) FROM (SELECT DISTINCT ""VolumeID"" FROM ({0} UNION {1}))",
                    missingBlockInfo,
                    missingBlocklistEntries);
                        
                if (passNo == 0)
                {
                    // On the first pass, we select all the volumes we know we need,
                    // which may be an empty list
                    cmd.CommandText = string.Format(selectCommand + @" WHERE ""ID"" IN ({0})", missingBlocklistVolumes);
                    
                    // Reset the list
                    m_proccessedVolumes = new Dictionary<long, long>();
                }
                else
                {
                    //On anything but the first pass, we check if we are done
                    var r = cmd.ExecuteScalarInt64(countMissingInformation, 0);
                    if (r == 0)
                        yield break;
                    
                    if (passNo == 1)
                    {
                        // On the second pass, we select all volumes that are not mentioned in the db
                        
                        var mentionedVolumes =
                            @"SELECT DISTINCT ""VolumeID"" FROM ""Block"" ";
                        
                        cmd.CommandText = string.Format(selectCommand + @" WHERE ""ID"" NOT IN ({0}) AND ""Type"" = ? ", mentionedVolumes);
                        cmd.AddParameter(RemoteVolumeType.Blocks.ToString());
                    }
                    else
                    {
                        // On the final pass, we select all volumes
                        // the filter will ensure that we do not download anything twice
                        cmd.CommandText = selectCommand + @" WHERE ""Type"" = ?";
                        cmd.AddParameter(RemoteVolumeType.Blocks.ToString());
                    }
                }
                
                using(var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {

                        var volumeID = rd.GetInt64(3);
                        
                        // Guard against multiple downloads of the same file
                        if (!m_proccessedVolumes.ContainsKey(volumeID))
                        {
                            m_proccessedVolumes.Add(volumeID, volumeID);
                            
                            yield return new RemoteVolume(
                                rd.GetString(0),
                                rd.ConvertValueToString(1),
                                rd.ConvertValueToInt64(2, -1)
                            );
                        }
                    }
                }
                
                
            }
        }
        
        public override void Dispose()
        {                        
            using (var cmd = m_connection.CreateCommand())
            {                    
                if (m_tempblocklistTab != null)
                    try
                    {
                        cmd.CommandText = string.Format(@"DROP TABLE IF EXISTS ""{0}""", m_tempblocklistTab);
                        cmd.ExecuteNonQuery();
                    }
                    catch { }
                    finally { m_tempblocklistTab = null; }
                
                if (m_temppendingblocksTab != null)
                    try
                    {
                        cmd.CommandText = string.Format(@"DROP TABLE IF EXISTS ""{0}""", m_temppendingblocksTab);
                        cmd.ExecuteNonQuery();
                    }
                    catch { }
                    finally { m_temppendingblocksTab = null; }
            }
            
            foreach(var cmd in new IDisposable [] {
                m_insertFileCommand,
                m_insertFilesetEntryCommand,
                m_insertMetadatasetCommand,
                m_insertBlocksetCommand,
                m_insertBlocksetEntry,
                m_insertBlocklistEntry,
                m_updateBlockVolumeCommand,
                m_insertBlocklist,
                m_insertBlockSetEntriesFromBlocklists,
                m_insertBlockSetEntriesForSingleBlockset,
                m_findBlocksetCommand,
                m_findBlocksetEntriesCommand,
                m_findBlocklistEntriesCommand,
                m_findMetadatasetCommand,
                m_findFilesetCommand,
                m_findblocklisthashCommand,
                m_findHashBlockCommand,
                m_insertBlockCommand,
                m_insertDuplicateBlockCommand,
                m_truncatePendingBlockCommand,
                m_insertPendingBlockCommand,
                m_updatePendingBlockIdsCommand,
                m_insertNewBlocksFromPendingCommand,
                m_updateBlockSizesFromPendingCommand,
                m_updateVolIdsFromPendingCommand,
                m_insertDuplicateBlocksFromPendingCommand
                })
                    try
                    {
                        if (cmd != null)
                            cmd.Dispose();
                    }
                    catch
                    {
                    }
                    
            base.Dispose();
        }
    }
}
