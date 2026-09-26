using AppleDrive.Application.Services;
using AppleDrive.Domain.Enums;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Application;

public sealed class DestinationLayoutTests
{
    private static readonly DateTime Taken = new(2026, 9, 5, 14, 3, 0);

    [Theory]
    [InlineData(FolderOrganization.Flat, @"D:\Photos")]
    [InlineData(FolderOrganization.YearMonth, @"D:\Photos\2026\09 September")]
    [InlineData(FolderOrganization.YearMonthDay, @"D:\Photos\2026\09 September\05")]
    public void Folder_follows_the_organization(FolderOrganization organization, string expected)
    {
        Assert.Equal(expected, DestinationLayout.GetFolder(@"D:\Photos", organization, Taken));
    }

    [Fact]
    public void Day_folders_are_not_invented_when_only_the_month_is_known()
    {
        Assert.Equal(@"D:\Photos\2026\09 September", DestinationLayout.GetFolder(@"D:\Photos", FolderOrganization.YearMonthDay, Taken, dayKnown: false));
    }

    [Theory]
    [InlineData("Internal Storage/202504__", 2025, 4)]
    [InlineData("Internal Storage/202409_a", 2024, 9)]
    [InlineData("201808__", 2018, 8)]
    public void Camera_roll_folder_names_give_a_month(string folder, int year, int month)
    {
        Assert.Equal(new DateTime(year, month, 1), DestinationLayout.MonthFromPhoneFolder(folder));
    }

    [Theory]
    [InlineData("Internal Storage/DCIM/100APPLE")]
    [InlineData("Internal Storage/202413__")]
    [InlineData("Internal Storage/199912__")]
    [InlineData("Internal Storage/2025041_")]
    [InlineData("Internal Storage/202504_A")]
    [InlineData("")]
    public void Other_folder_names_give_no_month(string folder)
    {
        Assert.Null(DestinationLayout.MonthFromPhoneFolder(folder));
    }

    [Fact]
    public void Month_names_do_not_depend_on_the_display_language()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");

            Assert.EndsWith(@"2026\09 September", DestinationLayout.GetFolder(@"D:\Photos", FolderOrganization.YearMonth, Taken));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("IMG_1234.HEIC", "IMG_1234.HEIC")]
    [InlineData("a/b\\c.jpg", "a_b_c.jpg")]
    [InlineData("what?.jpg", "what_.jpg")]
    [InlineData("CON.jpg", "_CON.jpg")]
    [InlineData("com1.MOV", "_com1.MOV")]
    [InlineData(".jpg", "IMG.jpg")]
    [InlineData("trailing. ", "trailing")]
    public void File_names_are_made_safe(string input, string expected)
    {
        Assert.Equal(expected, DestinationLayout.SanitizeFileName(input));
    }

    [Fact]
    public void Clashing_names_get_increasing_numbers()
    {
        using var directory = new TemporaryDirectory();
        var reservations = new DestinationNameReservations();
        File.WriteAllText(directory.Combine("IMG.jpg"), "existing");

        var first = reservations.Reserve(directory.Path, "IMG", [".jpg"])[0];
        File.WriteAllText(first, "copied");
        reservations.Release(first);
        var second = reservations.Reserve(directory.Path, "IMG", [".jpg"])[0];

        Assert.Equal(directory.Combine("IMG (1).jpg"), first);
        Assert.Equal(directory.Combine("IMG (2).jpg"), second);
    }

    [Fact]
    public void Names_promised_to_another_transfer_are_not_reused()
    {
        using var directory = new TemporaryDirectory();
        var reservations = new DestinationNameReservations();

        var first = reservations.Reserve(directory.Path, "IMG", [".jpg"])[0];
        var second = reservations.Reserve(directory.Path, "IMG", [".jpg"])[0];

        Assert.Equal(directory.Combine("IMG.jpg"), first);
        Assert.Equal(directory.Combine("IMG (1).jpg"), second);
        Assert.True(reservations.IsReserved(first));
        reservations.Release(first);
        Assert.False(reservations.IsReserved(first));
    }

    [Fact]
    public void Live_photo_parts_get_one_shared_name_free_for_both()
    {
        using var directory = new TemporaryDirectory();
        var reservations = new DestinationNameReservations();
        File.WriteAllText(directory.Combine("IMG_1.MOV"), "existing video only");

        var names = reservations.Reserve(directory.Path, "IMG_1", [".HEIC", ".MOV"]);

        Assert.Equal([directory.Combine("IMG_1 (1).HEIC"), directory.Combine("IMG_1 (1).MOV")], names);
    }

    [Fact]
    public void Names_are_case_insensitive_like_windows()
    {
        using var directory = new TemporaryDirectory();
        var reservations = new DestinationNameReservations();

        reservations.Reserve(directory.Path, "IMG", [".JPG"]);
        var second = reservations.Reserve(directory.Path, "img", [".jpg"])[0];

        Assert.Equal(directory.Combine("img (1).jpg"), second);
    }

    [Fact]
    public void Simultaneous_reservations_never_share_a_name()
    {
        using var directory = new TemporaryDirectory();
        var reservations = new DestinationNameReservations();
        var names = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 200, _ => names.Add(reservations.Reserve(directory.Path, "IMG", [".jpg"])[0]));

        Assert.Equal(200, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
