using OpenScanner.Server.Models;
using Microsoft.Data.Sqlite;

namespace OpenScanner.Server;

public interface IDatabase
{
    SqliteConnection GetConnection();
    Task<IEnumerable<Channel>> GetAllChannelsAsync();
    Task<IEnumerable<Channel>> GetChannelsNearAsync(double lat, double lon);
    Task<int> AddChannelAsync(Channel channel);
    Task UpdateChannelAsync(Channel channel);
    Task DeleteChannelAsync(int id);
    Task SaveTransmissionAsync(CallLog log);
    Task<IEnumerable<CallLog>> GetHistoryAsync(int limit = 100);
    Task<IEnumerable<string>> GetTransmissionYearsAsync();
    Task<IEnumerable<string>> GetTransmissionMonthsAsync(string year);
    Task<IEnumerable<string>> GetTransmissionDaysAsync(string year, string month);
    Task<IEnumerable<dynamic>> GetTransmissionChannelsAsync(string year, string month, string day);
    Task<IEnumerable<CallLog>> GetTransmissionsAsync(string year, string month, string day, string alphaTag, double frequency);
    Task<IEnumerable<CallLog>> SearchTransmissionsAsync(string query);
    Task DeleteTransmissionAsync(string id);
}