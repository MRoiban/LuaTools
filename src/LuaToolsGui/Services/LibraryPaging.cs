namespace LuaToolsGui.Services;
public static class LibraryPaging
{
    public static int TotalPages(int count, int size) => size == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(count / (double)size));
    public static List<T> Slice<T>(IReadOnlyList<T> items, int page, int size) => size == 0 ? items.ToList() : items.Skip((page - 1) * size).Take(size).ToList();
}
