using System;
using System.Collections.Generic;

namespace InterviewCopilot
{
    /// <summary>
    /// The answers given in this session, and which one is on screen.
    ///
    /// Why this exists: an interview is live, and the thing on screen can be
    /// taken away in a second. Someone coughs, a recruiter's colleague says a
    /// sentence in the background, the transcript catches it, and the answer the
    /// candidate was halfway through reading is replaced by an answer to a
    /// question nobody asked. Until now that answer was gone. They could not ask
    /// for it again without spending another question, and they could not read
    /// it because it had already been overwritten.
    ///
    /// So every answer is kept for the session and the last one is never the
    /// only one reachable. Going back is instant, comes from memory, costs no
    /// credits and makes no network call, because the moment it is needed is the
    /// moment there is no time.
    ///
    /// The one rule that is not obvious, and matters most in a live interview:
    ///
    ///   While the user is reading an older answer, a new one does not snap the
    ///   screen away from them. It is added and the counter grows, so they can
    ///   see something new arrived, and they return to it when they are ready.
    ///   Following the newest resumes automatically once they are back at it.
    ///
    /// The Mac's version shows each new answer immediately. That is right when
    /// nobody is browsing and wrong when they are: it recreates the exact
    /// problem this feature was built to solve, one keystroke later.
    /// </summary>
    internal sealed class AnswerHistory
    {
        /// <summary>
        /// How many answers are kept. Sixty is far more than any interview
        /// produces, and small enough that the text cannot grow into real
        /// memory: these are answers, not transcripts.
        /// </summary>
        internal const int MaxEntries = 60;

        internal readonly record struct Entry(string Question, string Answer);

        private readonly List<Entry> _items = new();

        /// <summary>Which answer is on screen. -1 when there is nothing yet.</summary>
        private int _index = -1;

        /// <summary>
        /// True while the screen should show each new answer as it arrives.
        /// Turned off by stepping back, turned on again by returning to the
        /// newest answer.
        /// </summary>
        private bool _following = true;

        internal int Count => _items.Count;

        /// <summary>The position of what is on screen, counting from one, for display.</summary>
        internal int Position => _index < 0 ? 0 : _index + 1;

        internal bool CanGoBack => _index > 0;

        internal bool CanGoForward => _index >= 0 && _index < _items.Count - 1;

        internal bool IsFollowingNewest => _following;

        /// <summary>
        /// How many answers arrived while the user was reading an older one. The
        /// number that tells them something is waiting without moving anything.
        /// </summary>
        internal int NewerWaiting => _following || _index < 0 ? 0 : _items.Count - 1 - _index;

        /// <summary>What should be on screen, or null when nothing has been answered.</summary>
        internal Entry? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        /// <summary>
        /// Records an answer. Returns true when the caller should put it on
        /// screen, which is every time except while the user is reading
        /// something older.
        /// </summary>
        internal bool Append(string question, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return false;

            _items.Add(new Entry(question ?? string.Empty, answer));

            if (_items.Count > MaxEntries)
            {
                _items.RemoveAt(0);
                // The oldest is gone, so everything after it moved down one. Keep
                // pointing at the same answer rather than at whatever slid into
                // its place.
                if (_index > 0) _index--;
            }

            if (_following)
            {
                _index = _items.Count - 1;
                return true;
            }

            return false;
        }

        /// <summary>Steps to the previous answer, or null when there is none.</summary>
        internal Entry? Back()
        {
            if (!CanGoBack) return null;

            _index--;
            _following = false;
            return _items[_index];
        }

        /// <summary>
        /// Steps to the next answer, or null when already at the newest. Arriving
        /// at the newest resumes following, so the next answer appears by itself.
        /// </summary>
        internal Entry? Forward()
        {
            if (!CanGoForward) return null;

            _index++;
            if (_index == _items.Count - 1) _following = true;
            return _items[_index];
        }

        /// <summary>
        /// Jumps straight to the newest answer and follows again. What the
        /// counter does when pressed, for someone who stepped back six answers
        /// and wants out in one action.
        /// </summary>
        internal Entry? JumpToNewest()
        {
            if (_items.Count == 0) return null;

            _index = _items.Count - 1;
            _following = true;
            return _items[_index];
        }

        /// <summary>
        /// Empties the history. Called where the conversation itself is cleared,
        /// so the arrows never offer answers from a conversation the user has
        /// deliberately ended.
        /// </summary>
        internal void Clear()
        {
            _items.Clear();
            _index = -1;
            _following = true;
        }

        /// <summary>
        /// What the counter reads. "3 of 7" while browsing, nothing at all until
        /// there is more than one answer to move between.
        /// </summary>
        internal string Label() => _items.Count <= 1 ? string.Empty : $"{Position} of {_items.Count}";
    }
}
