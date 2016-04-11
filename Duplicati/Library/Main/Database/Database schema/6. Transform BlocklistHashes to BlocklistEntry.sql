
BEGIN TRANSACTION;

/*
Blocklists are indexed blocks storing a list of hashes
of the real blocks for blockset reconstruction.
They are grouped by the BlocksetID
and ordered by the index.
From Db v6 replaces table "BlocklistHash"
["WITHOUT ROWID" available since SQLite v3.8.2 (= System.Data.SQLite v1.0.90.0, rel 2013-12-23)]
*/
CREATE TABLE "BlocklistEntry" (
	"BlocksetID" INTEGER NOT NULL,
	"Index" INTEGER NOT NULL,
	"BlockID" INTEGER NOT NULL,
	CONSTRAINT "BlocklistEntry_PK_IdIndex" PRIMARY KEY ("BlocksetID", "Index")
) {#if sqlite_version >= 3.8.2} WITHOUT ROWID {#endif};
/* As this table is a cross table we need fast lookup */

/**
 * Insert potentially missing blocks in "Block".
 * This should normally not happen (db is broken)
 * but allows db verification to later recognize errors (missing blocks).
 * Note: As we do not know (and cannot calculate) the block size here, we write -1.
 */
INSERT INTO "Block" ( "Hash", "Size" ,"VolumeID")
SELECT "MissingBlockHashes"."Hash", -1, -1
  FROM (SELECT DISTINCT "BlocklistHash"."Hash", -1, -1
          FROM "BlocklistHash" 
         WHERE NOT EXISTS (SELECT * FROM "Block" "B" WHERE "B"."Hash" = "BlocklistHash"."Hash")
		) "MissingBlockHashes" ;

/**
 * Now convert the hashes recorded to BlockID and insert them into the new 
 * "BlocklistEntry" table.
 * There is a (very) remote chance that there are hash-collisions on blocklist hashes
 * which do not hurt Duplicati because normally the size of a block is considered.
 * On cases like this, a db rebuild from scratch shall be performed.
 */
INSERT INTO "BlocklistEntry" ("BlocksetID", "Index", "BlockID")
SELECT "BlocklistHash"."BlocksetID", "BlocklistHash"."BlocklistHash"."index", IFNULL("Block"."ID", -1)  -- should not occur, as we just inserted them, but anyway (for testing).
  FROM "BlocklistHash" LEFT JOIN "Block" ON "Block"."Hash" = "BlocklistHash"."Hash";

DROP TABLE "BlocklistHash";

CREATE INDEX "BlocklistEntry_IndexIdsBackwards" ON "BlocklistEntry" ("BlockID");



/**** Process second upgrade: Directly use BlocksetId as MetadatasetId *****/

CREATE TABLE "MetadataBlockset" (
	"BlocksetID" INTEGER PRIMARY KEY
);

INSERT INTO "MetadataBlockset" ("BlocksetID")
SELECT DISTINCT "BlocksetID" FROM "Metadataset";

UPDATE "File" SET "MetadataID" = (SELECT "BlocksetID" FROM "Metadataset" WHERE "ID" = "MetadataID");

DROP TABLE "Metadataset";

-- Create View? "Metadataset" with ID and "BlocksetID"?

UPDATE "Version" SET "Version" = 6;

COMMIT;

/* As we deleted a rather large table, it is good practice to compact db. */
VACUUM;
