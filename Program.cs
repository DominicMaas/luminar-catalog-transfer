using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

// Ensure the arguments are passed in correctly
if (args.Length != 2)
{
    Console.WriteLine("Usage: <source path> <destination path>");
    return;
}

var sourcePath = args[0];
var destinationPath = args[1];

// Open up a connection to the source 
await using var sourceConnection = new SqliteConnection($"DataSource={sourcePath}");

var imageHistoryStates = (await sourceConnection.QueryAsync("SELECT * FROM img_history_states")).ToList();
var imageHistoryStateProxy = await sourceConnection.QueryAsync("SELECT * FROM image_history_state_proxy");

// image_id - image history pair
var imageHistory = new Dictionary<long, ImageHistory>();

Console.WriteLine("Matching image history with image history proxy...");
foreach (var proxy in imageHistoryStateProxy)
{
    // Ensure this image is added to the dictionary
    if (!imageHistory.ContainsKey(proxy._image_id_int_64))
        imageHistory[proxy._image_id_int_64] = new ImageHistory();
    
    // Current set
    if (proxy.current == 1)
    {
        imageHistory[proxy._image_id_int_64].CurrentId = proxy._state_id_int_64;
    }

    // This should always have a match!
    var historyState = imageHistoryStates.First(x => x._id_int_64 == proxy._state_id_int_64);
    imageHistory[proxy._image_id_int_64].History.Add(historyState);
}

// Retrieve the appropriate path from the images table (this allows us to link up the new catalog)

Console.WriteLine("Pairing up image history with actual image...");
foreach (var history in imageHistory)
{
    var image = await sourceConnection.QueryFirstAsync("SELECT * FROM images WHERE _id_int_64 = @id", new
    {
        id = history.Key
    });

    history.Value.Path = image.path_wide_ch;
}

Console.WriteLine("Finding matching image ids in destination catalog...");

await using var destinationConnection = new SqliteConnection($"DataSource={destinationPath}");
await destinationConnection.OpenAsync();

var destinationImages = (await destinationConnection.QueryAsync("SELECT * FROM images")).ToList();

// We no longer care about the ID, it is no longer needed
var imageHistoryList = imageHistory.Select(x => x.Value).ToList();
imageHistory.Clear(); // We are going to reuse this

// Rematch based on our new ID
foreach (var item in imageHistoryList)
{
    var match = destinationImages.First(x => x.path_wide_ch == item.Path);
    imageHistory[match._id_int_64] = item;
}

await using var writeTransaction = destinationConnection.BeginTransaction();

try
{
    // Write the edits into the correct table
    foreach (var item in imageHistory)
    {
        foreach (var history in item.Value.History)
        {
            // var currentMaxId = await destinationConnection.QueryFirstAsync<int>("SELECT MAX(_id_int_64) FROM img_history_states");
            // currentMaxId += 1; // Increment by one for the new max
        
            // Insert a history state
            var insertedId = await destinationConnection.QueryFirstAsync<long>(
                "INSERT INTO img_history_states (marked_to_delete_bool, name_wide_ch, data_wide_ch, guid_wide_ch, crop_info_wide_ch, image_orientation_int_64, hash_wide_ch, original_bool) VALUES(@marked_to_delete_bool, @name_wide_ch, @data_wide_ch, @guid_wide_ch, @crop_info_wide_ch, @image_orientation_int_64, @hash_wide_ch, @original_bool); SELECT last_insert_rowid()",
                new
                {
                    history.marked_to_delete_bool, 
                    history.name_wide_ch, 
                    history.data_wide_ch, 
                    history.guid_wide_ch, 
                    history.crop_info_wide_ch, 
                    history.image_orientation_int_64, 
                    history.hash_wide_ch, 
                    history.original_bool
                }, commandType: CommandType.Text, transaction: writeTransaction);
        
            // Insert a proxy
            await destinationConnection.ExecuteAsync(
                "INSERT INTO image_history_state_proxy (_image_id_int_64, _state_id_int_64, current) VALUES(@ImageId, @StateId, @Current)",
                new
                {
                    ImageId = item.Key,
                    StateId = insertedId,
                    Current = (history._id_int_64 == item.Value.CurrentId) ? 1 : 0
                }, commandType: CommandType.Text, transaction: writeTransaction);
        }
    }

    await writeTransaction.CommitAsync();
}
catch (Exception e)
{
    await writeTransaction.RollbackAsync();
    Console.WriteLine("Something went wrong! Rolling back changes: " + e.Message);
}

class ImageHistory
{
    public long CurrentId { get; set; }
    public string Path { get; set; }
    public List<dynamic> History { get; set; } = new List<dynamic>();
}