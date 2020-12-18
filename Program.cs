using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
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
var resources = (await sourceConnection.QueryAsync("SELECT * FROM resources")).ToList();
var imgHistoryStatesResources = (await sourceConnection.QueryAsync("SELECT * FROM img_history_states_resources")).ToList();

// image_id - image history pair
var imageHistory = new Dictionary<long, ImageHistory>();

Console.WriteLine("Matching image history with image history proxy...");
foreach (var proxy in imageHistoryStateProxy)
{
    // Ensure this image is added to the dictionary
    if (!imageHistory.ContainsKey(proxy._image_id_int_64))
        imageHistory[proxy._image_id_int_64] = new ImageHistory();

    var item = (ImageHistory)imageHistory[proxy._image_id_int_64];
    
    // Current set
    if (proxy.current == 1)
    {
        item.CurrentId = proxy._state_id_int_64;
    }

    // This should always have a match!
    var historyState = imageHistoryStates.First(x => x._id_int_64 == proxy._state_id_int_64);
    var historyResource = new HistoryResource
    {
        History = historyState
    };
    
    // Loop through each link and find the resource
    foreach (var id in imgHistoryStatesResources.Where(x => x._key_id_int_64 == historyState._id_int_64))
    {
        var resource = resources.First(x => x._id_int_64 == id._val_id_int_64);
        historyResource.Resources.Add(resource);
    }
    
    item.History.Add(historyResource);
    
    var i = 0;
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
                    history.History.marked_to_delete_bool, 
                    history.History.name_wide_ch, 
                    history.History.data_wide_ch, 
                    history.History.guid_wide_ch, 
                    history.History.crop_info_wide_ch, 
                    history.History.image_orientation_int_64, 
                    history.History.hash_wide_ch, 
                    history.History.original_bool
                }, commandType: CommandType.Text, transaction: writeTransaction);
        
            // Insert a proxy
            await destinationConnection.ExecuteAsync(
                "INSERT INTO image_history_state_proxy (_image_id_int_64, _state_id_int_64, current) VALUES(@ImageId, @StateId, @Current)",
                new
                {
                    ImageId = item.Key,
                    StateId = insertedId,
                    Current = (history.History._id_int_64 == item.Value.CurrentId) ? 1 : 0
                }, commandType: CommandType.Text, transaction: writeTransaction);
            
            // Loop through resources
            foreach (var resource in history.Resources)
            {
                // Strip out everything apart from the name
                var pathArr = ((string)resource.path_wide_ch).Split('/', '\\');
                var newPath = pathArr[pathArr.Length - 1];

                // Insert resource
                var resId = await destinationConnection.QueryFirstAsync<long>(
                    "INSERT INTO resources (marked_to_delete_bool, path_wide_ch) VALUES(@marked_to_delete_bool, @path_wide_ch); SELECT last_insert_rowid()", new
                    {
                        resource.marked_to_delete_bool,
                        path_wide_ch = newPath
                    }, transaction: writeTransaction);
                
                // Insert resource history pair
                await destinationConnection.ExecuteAsync(
                    "INSERT INTO img_history_states_resources (_key_id_int_64, _val_id_int_64) VALUES(@_key_id_int_64, @_val_id_int_64)",
                    new
                    {
                        _key_id_int_64 = insertedId,
                        _val_id_int_64 = resId
                    }, commandType: CommandType.Text, transaction: writeTransaction);
            }
            
            
        }
    }

    //await writeTransaction.CommitAsync();
    await writeTransaction.RollbackAsync(); // TESTING
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
    public List<HistoryResource> History { get; set; } = new List<HistoryResource>();
}

class HistoryResource
{
    public List<dynamic> Resources { get; set; } = new List<dynamic>();
    public dynamic History { get; set; }
}