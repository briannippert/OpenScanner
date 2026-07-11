using OpenScanner.Server.Models;
using Microsoft.Data.Sqlite;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// Interface for database operations.
/// </summary>
public interface IDatabase
{
    /// <summary>
    /// Gets a new SQLite connection.
    /// </summary>
    /// <returns>A configured <see cref="SqliteConnection"/>.</returns>
    SqliteConnection GetConnection();

    /// <summary>
    /// Retrieves all configured channels.
    /// </summary>
    /// <returns>A collection of channels.</returns>
    Task<IEnumerable<Channel>> GetAllChannelsAsync();

    /// <summary>
    /// Retrieves channels near a specific geographic location.
    /// </summary>
    /// <param name="lat">Latitude.</param>
    /// <param name="lon">Longitude.</param>
    /// <returns>A collection of nearby channels.</returns>
    Task<IEnumerable<Channel>> GetChannelsNearAsync(double lat, double lon);

    /// <summary>
    /// Adds a new channel to the database.
    /// </summary>
    /// <param name="channel">The channel to add.</param>
    /// <returns>The ID of the newly created channel.</returns>
    Task<int> AddChannelAsync(Channel channel);

    /// <summary>
    /// Updates an existing channel.
    /// </summary>
    /// <param name="channel">The channel with updated values.</param>
    Task UpdateChannelAsync(Channel channel);

    /// <summary>
    /// Deletes a channel by ID.
    /// </summary>
    /// <param name="id">The ID of the channel to delete.</param>
    Task DeleteChannelAsync(int id);

    /// <summary>
    /// Saves a transmission log entry.
    /// </summary>
    /// <param name="log">The transmission log.</param>
    Task SaveTransmissionAsync(CallLog log);

    /// <summary>
    /// Updates only the transcription field of a transmission log entry.
    /// </summary>
    /// <param name="id">The transmission ID.</param>
    /// <param name="transcription">The transcription text.</param>
    Task UpdateTranscriptionAsync(string id, string? transcription);

    /// <summary>
    /// Retrieves the most recent transmission logs.
    /// </summary>
    /// <param name="limit">Maximum number of logs to return.</param>
    /// <returns>A collection of call logs.</returns>
    Task<IEnumerable<CallLog>> GetHistoryAsync(int limit = 100);

    /// <summary>
    /// Retrieves a single transmission log by its ID.
    /// </summary>
    /// <param name="id">The transmission ID.</param>
    /// <returns>The matching call log, or null if not found.</returns>
    Task<CallLog?> GetTransmissionByIdAsync(string id);

    /// <summary>
    /// Retrieves the IDs of the oldest non-favorite transmissions, oldest first.
    /// Used by low-disk cleanup to purge recordings while preserving favorites.
    /// </summary>
    /// <param name="limit">Maximum number of IDs to return.</param>
    /// <returns>A collection of transmission IDs, oldest first.</returns>
    Task<IEnumerable<string>> GetOldestTransmissionIdsAsync(int limit);

    /// <summary>
    /// Gets all years that have transmission data.
    /// </summary>
    /// <returns>A collection of years as strings.</returns>
    Task<IEnumerable<string>> GetTransmissionYearsAsync();

    /// <summary>
    /// Gets all months that have transmission data for a given year.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <returns>A collection of months as strings.</returns>
    Task<IEnumerable<string>> GetTransmissionMonthsAsync(string year);

    /// <summary>
    /// Gets all days that have transmission data for a given year and month.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <returns>A collection of days as strings.</returns>
    Task<IEnumerable<string>> GetTransmissionDaysAsync(string year, string month);

    /// <summary>
    /// Gets active channels and counts for a specific day.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>A collection of channel stats.</returns>
    Task<IEnumerable<dynamic>> GetTransmissionChannelsAsync(string year, string month, string day);

    /// <summary>
    /// Retrieves transmissions matching specific criteria.
    /// </summary>
    /// <param name="year">Year.</param>
    /// <param name="month">Month.</param>
    /// <param name="day">Day.</param>
    /// <param name="alphaTag">Channel Alpha Tag.</param>
    /// <param name="frequency">Frequency in MHz.</param>
    /// <returns>A collection of matching call logs.</returns>
    Task<IEnumerable<CallLog>> GetTransmissionsAsync(string year, string month, string day, string alphaTag, double frequency);

    /// <summary>
    /// Searches transmissions for a text query.
    /// </summary>
    /// <param name="query">The text to search for.</param>
    /// <returns>A collection of matching call logs.</returns>
    Task<IEnumerable<CallLog>> SearchTransmissionsAsync(string query);

    /// <summary>
    /// Retrieves all favorited transmission logs.
    /// </summary>
    /// <returns>A collection of favorited call logs.</returns>
    Task<IEnumerable<CallLog>> GetFavoritesAsync();

    /// <summary>
    /// Sets or clears the favorite flag on a transmission.
    /// </summary>
    /// <param name="id">The transmission ID.</param>
    /// <param name="isFavorite">True to favorite, false to unfavorite.</param>
    Task SetFavoriteAsync(string id, bool isFavorite);

    /// <summary>
    /// Deletes a specific transmission log.
    /// </summary>
    /// <param name="id">The ID of the transmission.</param>
    Task DeleteTransmissionAsync(string id);

    /// <summary>
    /// Clears all transmission history.
    /// </summary>
    Task ClearHistoryAsync();

    /// <summary>
    /// Gets aggregate recording/transcription statistics for diagnostics:
    /// total recordings, how many are transcribed, and the oldest/newest timestamps.
    /// </summary>
    Task<DbStats> GetDbStatsAsync();

    /// <summary>
    /// Gets recordings with a missing (null/empty) transcription whose timestamp is
    /// at or after <paramref name="sinceUtc"/>. Used by the transcription backfill job.
    /// </summary>
    Task<IEnumerable<CallLog>> GetUntranscribedSinceAsync(DateTime sinceUtc);

    // Fire Tones
    
    /// <summary>
    /// Retrieves all fire tone sets.
    /// </summary>
    /// <returns>A collection of fire tone sets.</returns>
    Task<IEnumerable<FireToneSet>> GetAllFireTonesAsync();

    /// <summary>
    /// Adds a new fire tone set.
    /// </summary>
    /// <param name="tone">The tone set to add.</param>
    /// <returns>The ID of the new tone set.</returns>
    Task<int> AddFireToneAsync(FireToneSet tone);

    /// <summary>
    /// Updates an existing fire tone set.
    /// </summary>
    /// <param name="tone">The updated tone set.</param>
    Task UpdateFireToneAsync(FireToneSet tone);

    /// <summary>
    /// Deletes a fire tone set.
    /// </summary>
    /// <param name="id">The ID of the tone set to delete.</param>
    Task DeleteFireToneAsync(int id);

    // Radio Events (fire tone-out and MDC1200 detections)

    /// <summary>
    /// Persists a decoded signaling event (fire tone-out or MDC1200 packet).
    /// </summary>
    /// <param name="e">The event to store.</param>
    Task AddRadioEventAsync(RadioEvent e);

    /// <summary>
    /// Retrieves the most recent radio events, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of events to return.</param>
    Task<IEnumerable<RadioEvent>> GetRadioEventsAsync(int limit = 100);

    /// <summary>
    /// Clears all stored radio events.
    /// </summary>
    Task ClearRadioEventsAsync();

    // Settings

    /// <summary>
    /// Retrieves a system setting by key.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <returns>The setting value, or null if not found.</returns>
    Task<string?> GetSettingAsync(string key);

    /// <summary>
    /// Sets a system setting value.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The value to set.</param>
    Task SetSettingAsync(string key, string value);

    /// <summary>
    /// Retrieves all system settings.
    /// </summary>
    /// <returns>A dictionary of all settings.</returns>
    Task<Dictionary<string, string>> GetAllSettingsAsync();
}