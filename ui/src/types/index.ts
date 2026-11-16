export const money = (n: number) =>
  new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: "INR",
    maximumFractionDigits: 2,
  }).format(n);
export const time = (s: string) =>
  new Date(/[zZ]$|[+-]\d\d:\d\d$/.test(s) ? s : s + "Z").toLocaleString("en-IN", {
    timeZone: "Asia/Kolkata",
  });
