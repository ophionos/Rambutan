namespace Rambutan.Core.Models;

/// <summary>
/// The user's thumbs on a track, as YouTube Music stores it (likeStatus
/// LIKE / DISLIKE / INDIFFERENT). None is a real answer ("not rated"), distinct
/// from a null <see cref="TrackInfo.Rating"/>, which means the source didn't say.
/// </summary>
public enum Rating { None, Like, Dislike }
