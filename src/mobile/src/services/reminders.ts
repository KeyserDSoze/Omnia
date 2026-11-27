import type { ReminderDto, ReminderRepeat } from './memoryApi';

/** The same rules as the server: text up to 200 characters, HH:mm, and a real date for a one-off. */
export function checkReminder(input: { text: string; time: string; repeat: ReminderRepeat; date: string }): boolean {
  const text = input.text.trim();
  if (text.length === 0 || text.length > 200 || /[\u0000-\u001f\u007f]/.test(text)) return false;
  if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(input.time)) return false;
  if (input.repeat !== 'once') return true;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(input.date);
  if (!match) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const check = new Date(Date.UTC(year, month - 1, day));
  return check.getUTCFullYear() === year && check.getUTCMonth() === month - 1 && check.getUTCDate() === day;
}

/** One line for the list: the text, how often and at what time. */
export function reminderLine(reminder: ReminderDto, repeatLabel: string): string {
  const when = reminder.repeat === 'once' && reminder.date ? `${reminder.date} ${reminder.time}` : reminder.time;
  return `${reminder.text} \u00b7 ${repeatLabel} \u00b7 ${when}`;
}

export function deviceTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
}
