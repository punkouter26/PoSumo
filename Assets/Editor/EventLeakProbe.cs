using System;
using System.Reflection;

namespace PoSumo.EditorTools
{
    /// Reflection subscriber counts for events, so a harness can assert that a
    /// loop left behind as many subscribers as it found.
    ///
    /// Why: "subscribe in OnEnable, unsubscribe in OnDisable — statics especially"
    /// is enforced by convention only, and Enter Play Mode domain reload is OFF in
    /// this project. A static event holding one destroyed handler survives the
    /// scene load into the next bout; nothing errors, the next match simply fires
    /// the dead delegate (or worse, a live one that belongs to a previous scene).
    /// The probe counts the invocation list of a field-like event's compiler
    /// generated backing field, which is named exactly like the event — that is
    /// the declaration shape of every event this project couples through
    /// (`Systems_GameMatchManager`'s four instance events and
    /// `Systems_BodyDamage`'s three statics).
    public static class EventLeakProbe
    {
        /// Subscriber count for a static event, or -1 if the event's backing
        /// field cannot be found (custom add/remove accessors change the shape).
        public static int StaticSubscribers(Type type, string eventName)
        {
            FieldInfo field = type.GetField(eventName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return Count(field, null);
        }

        /// Subscriber count for an instance event, or -1 if not found.
        public static int InstanceSubscribers(object instance, string eventName)
        {
            if (instance == null)
            {
                return -1;
            }
            FieldInfo field = instance.GetType().GetField(eventName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return Count(field, instance);
        }

        private static int Count(FieldInfo field, object target)
        {
            if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType))
            {
                return -1;
            }
            Delegate handler = field.GetValue(target) as Delegate;
            return handler == null ? 0 : handler.GetInvocationList().Length;
        }
    }
}
