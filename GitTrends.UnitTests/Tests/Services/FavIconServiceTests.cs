﻿namespace GitTrends.UnitTests;

class FavIconServiceTests : BaseTest
{
	[Test]
	public async Task GetFavIconImageSourceTest_InvalidUri()
	{
		//Arrange
		Uri invalidUri = new Uri("https://abc123456789qwertyuioplkjhgfdsazxcvbnm.com/");
		var favIconService = ServiceCollection.ServiceProvider.GetRequiredService<FavIconService>();

		//Act
		var fileImageSource = (FileImageSource)await favIconService.GetFavIconImageSource(invalidUri, CancellationToken.None).ConfigureAwait(false);

		//Assert
		Assert.Multiple(() =>
		{
			Assert.That(fileImageSource, Is.Not.Null);
			Assert.That(FavIconService.DefaultFavIcon, Is.EqualTo(fileImageSource.File));
		});
	}

	[TestCase("https://outlook.live.com/owa/", "https://logincdn.msftauth.net/16.000.30324.2/images/favicon.ico")] //Shortcut icon Uri
	[TestCase("https://chrissainty.com/", "https://chrissainty.com/favicon-32x32.png")] // Icon Url
	[TestCase("https://visualstudiomagazine.com/", "https://visualstudiomagazine.com/design/ECG/VisualStudioMagazine/img/vsm_apple_icon.png")] //Apple Touch Icon Url
	[TestCase("https://mondaypunday.com/", "https://mondaypunday.com/favicon.ico")] //FavIcon Url
	public async Task GetFavIconImageSourceTest_ValidUrl(string url, string expectedFavIconUrl)
	{
		//Arrange
		var favIconService = ServiceCollection.ServiceProvider.GetRequiredService<FavIconService>();

		//Act
		var uriImageSource = (UriImageSource)await favIconService.GetFavIconImageSource(new Uri(url), CancellationToken.None).ConfigureAwait(false);

		//Assert
		Assert.Multiple(() =>
		{
			Assert.That(uriImageSource, Is.Not.Null);
			Assert.That(uriImageSource.Uri, Is.EqualTo(new Uri(expectedFavIconUrl)));
		});
	}

	[TestCase("https://www.amazon.co.uk", "https://amazon.co.uk/favicon.ico")]
	public async Task GetFavIconImageSourceTest_CountryCodeTopLevelDomains(string url, string expectedFavIconUrl)
	{
		//Arrange
		var favIconService = ServiceCollection.ServiceProvider.GetRequiredService<FavIconService>();

		//Act
		var uriImageSource = (UriImageSource)await favIconService.GetFavIconImageSource(new Uri(url), CancellationToken.None).ConfigureAwait(false);

		//Assert
		Assert.Multiple(() =>
		{
			Assert.That(uriImageSource, Is.Not.Null);
			Assert.That(uriImageSource.Uri, Is.EqualTo(new Uri(expectedFavIconUrl)));
			Assert.That(uriImageSource.Uri.ToString().Contains("https://favicons.githubusercontent.com"), Is.False);
		});
	}
}